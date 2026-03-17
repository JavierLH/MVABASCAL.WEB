using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace SistemaAduanero.Web.Services
{
    /// <summary>
    /// Servicio singleton que gestiona la cola RabbitMQ para obtener el e-Document de VUCEM.
    /// Flujo: Encolar(anexoId, nOperacion, delay=8s) → esperar delay → llamar consultar-ejecutar
    ///        Si obtiene eDocument → guardar en BD + callback al componente
    ///        Si no → re-encolar con delay+5s (máximo 5 reintentos)
    /// </summary>
    public class VucemEdocumentService : IAsyncDisposable
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<VucemEdocumentService> _logger;

        private const string QueueName = "edocument_queue";
        private const int MaxReintentos = 5;

        private IConnection? _connection;
        private IChannel? _channel;
        private bool _consumerStarted = false;

        // Callbacks registrados por los componentes: key=AnexoId, value=acción a ejecutar al éxito
        private readonly Dictionary<int, Func<string, Task>> _callbacks = new();

        public VucemEdocumentService(IHttpClientFactory httpClientFactory, ILogger<VucemEdocumentService> logger)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        // ─── Inicialización lazy de la conexión ────────────────────────────────────

        private async Task EnsureConnectedAsync()
        {
            if (_connection != null && _connection.IsOpen) return;

            var factory = new ConnectionFactory
            {
                HostName = "localhost",
                UserName = "guest",
                Password = "guest"
            };

            _connection = await factory.CreateConnectionAsync();
            _channel = await _connection.CreateChannelAsync();

            await _channel.QueueDeclareAsync(
                queue: QueueName,
                durable: true,
                exclusive: false,
                autoDelete: false);

            _logger.LogInformation("VucemEdocumentService conectado a RabbitMQ.");
        }

        // ─── API pública ────────────────────────────────────────────────────────────

        /// <summary>
        /// Registra un callback que se ejecutará cuando el e-Document del anexo sea obtenido.
        /// </summary>
        public void RegistrarCallback(int anexoId, Func<string, Task> onEdocumentObtenido)
        {
            _callbacks[anexoId] = onEdocumentObtenido;
        }

        /// <summary>
        /// Desregistra el callback de un anexo (llamar al destruir el componente).
        /// </summary>
        public void DesregistrarCallback(int anexoId)
        {
            _callbacks.Remove(anexoId);
        }

        /// <summary>
        /// Encola una solicitud para obtener el e-Document.
        /// </summary>
        public async Task EnqueueAsync(int anexoId, long numeroOperacion, int delaySegundos = 8)
        {
            await EnsureConnectedAsync();
            await StartConsumerIfNeededAsync();

            var mensaje = new EdocumentMensaje
            {
                AnexoId = anexoId,
                NumeroOperacion = numeroOperacion,
                DelaySegundos = delaySegundos,
                Intento = 1
            };

            await PublicarMensajeAsync(mensaje);
            _logger.LogInformation("Encolado: AnexoId={AnexoId}, NOperacion={NOperacion}, Delay={Delay}s",
                anexoId, numeroOperacion, delaySegundos);
        }

        // ─── Internos ───────────────────────────────────────────────────────────────

        private async Task PublicarMensajeAsync(EdocumentMensaje mensaje)
        {
            if (_channel == null) throw new InvalidOperationException("Canal RabbitMQ no inicializado.");

            var json = JsonSerializer.Serialize(mensaje);
            var body = Encoding.UTF8.GetBytes(json);

            var props = new BasicProperties { Persistent = true };

            await _channel.BasicPublishAsync(
                exchange: string.Empty,
                routingKey: QueueName,
                mandatory: false,
                basicProperties: props,
                body: body);
        }

        private async Task StartConsumerIfNeededAsync()
        {
            if (_consumerStarted || _channel == null) return;
            _consumerStarted = true;

            var consumer = new AsyncEventingBasicConsumer(_channel);
            consumer.ReceivedAsync += async (_, ea) =>
            {
                EdocumentMensaje? mensaje = null;
                try
                {
                    var json = Encoding.UTF8.GetString(ea.Body.ToArray());
                    mensaje = JsonSerializer.Deserialize<EdocumentMensaje>(json);
                    if (mensaje == null)
                    {
                        await _channel.BasicAckAsync(ea.DeliveryTag, false);
                        return;
                    }

                    // Esperar el delay definido
                    await Task.Delay(TimeSpan.FromSeconds(mensaje.DelaySegundos));

                    // Llamar al endpoint de consultar-ejecutar
                    var edocument = await ConsultarEdocumentAsync(mensaje.NumeroOperacion);

                    if (!string.IsNullOrEmpty(edocument))
                    {
                        // Éxito: guardar en BD y notificar al componente
                        await GuardarEdocumentEnBdAsync(mensaje.AnexoId, edocument);

                        if (_callbacks.TryGetValue(mensaje.AnexoId, out var callback))
                            await callback(edocument);

                        await _channel.BasicAckAsync(ea.DeliveryTag, false);
                        _logger.LogInformation("e-Document obtenido: AnexoId={AnexoId}, eDoc={EDoc}", mensaje.AnexoId, edocument);
                    }
                    else if (mensaje.Intento < MaxReintentos)
                    {
                        // Re-encolar con +5 segundos
                        var reintento = mensaje with
                        {
                            Intento = mensaje.Intento + 1,
                            DelaySegundos = mensaje.DelaySegundos + 5
                        };
                        await PublicarMensajeAsync(reintento);
                        await _channel.BasicAckAsync(ea.DeliveryTag, false);
                        _logger.LogWarning("e-Document vacío. Reintento {Intento}/{Max} en {Delay}s.",
                            reintento.Intento, MaxReintentos, reintento.DelaySegundos);
                    }
                    else
                    {
                        // Máximos reintentos alcanzados — descartar
                        await _channel.BasicAckAsync(ea.DeliveryTag, false);
                        _logger.LogError("e-Document no obtenido tras {Max} reintentos para AnexoId={AnexoId}.",
                            MaxReintentos, mensaje?.AnexoId);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error procesando mensaje RabbitMQ para AnexoId={AnexoId}.", mensaje?.AnexoId);
                    await _channel.BasicNackAsync(ea.DeliveryTag, false, requeue: false);
                }
            };

            await _channel.BasicConsumeAsync(queue: QueueName, autoAck: false, consumer: consumer);
        }

        private async Task<string?> ConsultarEdocumentAsync(long numeroOperacion)
        {
            try
            {
                var client = _httpClientFactory.CreateClient("ApiClient");
                var response = await client.GetAsync($"api/APIVucem/consultar-ejecutar?numeroOperacion={numeroOperacion}");

                if (!response.IsSuccessStatusCode) return null;

                // La respuesta es el DTO de consulta — buscamos el campo eDocumentField
                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // Intentar navegar al campo eDocument según la estructura de la respuesta
                if (root.TryGetProperty("consultaDigitalizarDocumentoServiceResponse", out var consulta))
                    if (consulta.TryGetProperty("eDocumentField", out var edoc))
                    {
                        var val = edoc.GetString();
                        return string.IsNullOrWhiteSpace(val) ? null : val;
                    }

                // Alternativa: campo directo
                if (root.TryGetProperty("eDocumentField", out var edocDirect))
                {
                    var val = edocDirect.GetString();
                    return string.IsNullOrWhiteSpace(val) ? null : val;
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error al consultar e-Document para NOperacion={NOperacion}.", numeroOperacion);
                return null;
            }
        }

        private async Task GuardarEdocumentEnBdAsync(int anexoId, string edocument)
        {
            try
            {
                var client = _httpClientFactory.CreateClient("ApiClient");
                await client.PutAsJsonAsync($"api/Documentos/{anexoId}/edocument", edocument);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error al guardar e-Document en BD para AnexoId={AnexoId}.", anexoId);
            }
        }

        // ─── Dispose ────────────────────────────────────────────────────────────────

        public async ValueTask DisposeAsync()
        {
            if (_channel != null) await _channel.CloseAsync();
            if (_connection != null) await _connection.CloseAsync();
        }
    }

    // Mensaje que viaja en la cola
    public record EdocumentMensaje
    {
        public int AnexoId { get; init; }
        public long NumeroOperacion { get; init; }
        public int DelaySegundos { get; init; }
        public int Intento { get; init; }
    }
}
