namespace SistemaAduanero.Web.Models
{
    public class ManifestacionDto
    {
        public int ManifestacionId { get; set; }
        public string NumeroPedimento { get; set; }
        public string Cove { get; set; }
        public decimal TotalValorAduana { get; set; }
        public string EstadoEnvio { get; set; }
        public DateTime FechaRegistro { get; set; }
    }
}