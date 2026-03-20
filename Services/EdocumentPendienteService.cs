using System.Net.Http.Json;
using System.Text.Json;

namespace SistemaAduanero.Web.Services
{
    /// <summary>
    /// Servicio en background que detecta documentos con N. Operación pero sin e-Document
    /// y los consulta automáticamente en VUCEM. Expone callbacks para actualizar la UI en tiempo real.
    /// </summary>
    public class EdocumentPendienteService : BackgroundService
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<EdocumentPendienteService> _logger;

        // Intervalo entre cada ciclo completo de revisión
        private static readonly TimeSpan IntervaloRevision = TimeSpan.FromSeconds(10);

        // Tiempo de espera entre cada petición individual a VUCEM
        private static readonly TimeSpan DelayEntreConsultas = TimeSpan.FromSeconds(3);

        // Espera inicial antes de la primera ejecución
        private static readonly TimeSpan DelayInicial = TimeSpan.FromSeconds(10);

        // Callbacks registrados por los componentes: key=AnexoId, value=acción a ejecutar al éxito
        private readonly Dictionary<int, Func<string, Task>> _callbacks = new();

        public EdocumentPendienteService(
            IHttpClientFactory httpClientFactory,
            ILogger<EdocumentPendienteService> logger)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        // ─── API pública para que los componentes Blazor se suscriban ───────────────

        /// <summary>Registra un callback que se ejecuta cuando se obtiene el e-Document de un anexo.</summary>
        public void RegistrarCallback(int anexoId, Func<string, Task> onEdocumentObtenido)
            => _callbacks[anexoId] = onEdocumentObtenido;

        /// <summary>Desregistra el callback de un anexo (llamar al cerrar el componente).</summary>
        public void DesregistrarCallback(int anexoId)
            => _callbacks.Remove(anexoId);

        // ─── Bucle principal ─────────────────────────────────────────────────────────

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("=== EdocumentPendienteService INICIADO ===");

            await Task.Delay(DelayInicial, stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("--- Ciclo: buscando pendientes de e-Document ---");
                try
                {
                    await ProcesarPendientesAsync(stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    _logger.LogError(ex, "Error en ciclo de EdocumentPendienteService.");
                }

                await Task.Delay(IntervaloRevision, stoppingToken);
            }
        }

        private async Task ProcesarPendientesAsync(CancellationToken stoppingToken)
        {
            var client = _httpClientFactory.CreateClient("BackgroundApiClient");

            List<PendienteEdocumentDto>? pendientes = null;
            try
            {
                var res = await client.GetAsync("api/Documentos/pendientes-edocument", stoppingToken);
                var rawJson = await res.Content.ReadAsStringAsync(stoppingToken);
                _logger.LogInformation("Pendientes ({Status}): {Json}", res.StatusCode, rawJson);

                if (res.IsSuccessStatusCode)
                    pendientes = JsonSerializer.Deserialize<List<PendienteEdocumentDto>>(rawJson,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error al obtener pendientes.");
                return;
            }

            if (pendientes == null || pendientes.Count == 0)
            {
                _logger.LogInformation("No hay pendientes de e-Document.");
                return;
            }

            _logger.LogInformation("{Count} pendientes encontrados.", pendientes.Count);

            for (int i = 0; i < pendientes.Count; i++)
            {
                if (stoppingToken.IsCancellationRequested) break;

                var pendiente = pendientes[i];

                if (string.IsNullOrWhiteSpace(pendiente.NoOperacion) ||
                    !long.TryParse(pendiente.NoOperacion.Trim(), out long nOpLong))
                {
                    _logger.LogWarning("AnexoId={AnexoId} tiene NoOperacion inválido: '{Val}'. Se salta.",
                        pendiente.AnexoId, pendiente.NoOperacion);
                    continue;
                }

                try
                {
                    await ConsultarYGuardarEdocumentAsync(client, pendiente.AnexoId, nOpLong, stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error procesando AnexoId={AnexoId}.", pendiente.AnexoId);
                }

                if (i < pendientes.Count - 1)
                    await Task.Delay(DelayEntreConsultas, stoppingToken);
            }
        }

        private async Task ConsultarYGuardarEdocumentAsync(
            HttpClient client, int anexoId, long nOperacion, CancellationToken stoppingToken)
        {
            _logger.LogInformation("Consultando VUCEM AnexoId={AnexoId}, NOperacion={NOp}...", anexoId, nOperacion);

            string rawJson;
            try
            {
                var response = await client.GetAsync(
                    $"api/APIVucem/consultar-ejecutar?numeroOperacion={nOperacion}", stoppingToken);
                rawJson = await response.Content.ReadAsStringAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "HTTP error consultando VUCEM NOperacion={NOp}.", nOperacion);
                return;
            }

            _logger.LogInformation("VUCEM respuesta AnexoId={AnexoId}: {Json}", anexoId, rawJson);

            var edocument = ExtraerEdocument(rawJson);

            if (string.IsNullOrWhiteSpace(edocument))
            {
                _logger.LogWarning("e-Document no disponible aún para AnexoId={AnexoId}.", anexoId);
                return;
            }

            _logger.LogInformation("e-Document extraído: '{EDoc}' para AnexoId={AnexoId}", edocument, anexoId);

            // 1. Guardar en BD
            var putResponse = await client.PutAsJsonAsync(
                $"api/Documentos/{anexoId}/edocument",
                new { Edocument = edocument },
                stoppingToken);

            if (putResponse.IsSuccessStatusCode)
            {
                _logger.LogInformation("e-Document guardado en BD: AnexoId={AnexoId}", anexoId);

                // 2. Notificar al componente Blazor si está suscrito
                if (_callbacks.TryGetValue(anexoId, out var callback))
                {
                    try { await callback(edocument); }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error ejecutando callback UI para AnexoId={AnexoId}.", anexoId);
                    }
                }
            }
            else
            {
                _logger.LogError("Error al guardar e-Document en BD. AnexoId={AnexoId}, HTTP={Status}",
                    anexoId, (int)putResponse.StatusCode);
            }
        }

        private string? ExtraerEdocument(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // Intento 1: respuesta anidada
                if (root.TryGetProperty("consultaDigitalizarDocumentoServiceResponse", out var consulta))
                {
                    if (consulta.TryGetProperty("eDocumentField", out var e1)) return NullIfEmpty(e1.GetString());
                    if (consulta.TryGetProperty("eDocument", out var e2)) return NullIfEmpty(e2.GetString());
                }

                // Intento 2: campos directos en la raíz
                foreach (var name in new[] { "eDocumentField", "eDocument", "EDocumentField", "EDocument", "numeroEdocument", "NumeroEdocument" })
                    if (root.TryGetProperty(name, out var p)) return NullIfEmpty(p.GetString());

                // Intento 3: búsqueda recursiva nivel 1
                foreach (var child in root.EnumerateObject())
                    if (child.Value.ValueKind == JsonValueKind.Object)
                        foreach (var inner in child.Value.EnumerateObject())
                            if (inner.Name.IndexOf("document", StringComparison.OrdinalIgnoreCase) >= 0 &&
                                inner.Value.ValueKind == JsonValueKind.String)
                            {
                                var val = NullIfEmpty(inner.Value.GetString());
                                if (val != null) return val;
                            }

                return null;
            }
            catch { return null; }
        }

        private static string? NullIfEmpty(string? val) =>
            string.IsNullOrWhiteSpace(val) ? null : val;
    }

    public class PendienteEdocumentDto
    {
        public int AnexoId { get; set; }
        public string? NoOperacion { get; set; }
    }
}
