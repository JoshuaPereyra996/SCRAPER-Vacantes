using OccScraper.Models;

namespace OccScraper.Services;

/// <summary>
/// Opciones de ejecución comunes a cualquier scraper (mapeadas desde appsettings.json).
/// </summary>
public record OpcionesScraper(
    bool Headless,
    int TimeoutMs,
    int DelayMs,
    string UserAgent);

/// <summary>
/// Resultado de una búsqueda: el contenido crudo tal cual lo entregó el sitio (para
/// guardarlo sin pérdida) más la lista de vacantes ya parseadas.
/// </summary>
/// <param name="Crudo">Contenido crudo (JSON de OCC, HTML de Computrabajo, etc.).</param>
/// <param name="ExtensionCrudo">Extensión del archivo crudo: "json", "html"...</param>
/// <param name="Vacantes">Lista de vacantes limpias.</param>
public record ResultadoScrape(
    string Crudo,
    string ExtensionCrudo,
    List<Vacante> Vacantes);

/// <summary>
/// Contrato que implementa cada sitio de empleo soportado. Cada implementación se
/// encarga de su propia navegación, extracción y parseo a Vacante.
/// </summary>
public interface ISitioScraper
{
    /// <summary>Nombre/slug del sitio (ej. "occ", "computrabajo"). Se usa en el nombre de archivo.</summary>
    string Nombre { get; }

    /// <summary>Realiza UNA búsqueda y devuelve el resultado, o null si no se obtuvo nada.</summary>
    Task<ResultadoScrape?> BuscarAsync(string empleo, string ciudad);
}
