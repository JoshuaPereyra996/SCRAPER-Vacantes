using System.Text.Json.Serialization;

namespace OccScraper.Models;

/// <summary>
/// Modelo limpio de una vacante, listo para serializar al JSON de salida.
/// Se construye a partir del JSON crudo devuelto por /offer/search.
/// </summary>
public class Vacante
{
    /// <summary>Identificador de la vacante (jobid) tal como viene en el JSON.</summary>
    [JsonPropertyName("jobid")]
    public string? JobId { get; set; }

    /// <summary>Título del puesto.</summary>
    [JsonPropertyName("titulo")]
    public string? Titulo { get; set; }

    /// <summary>Nombre de la empresa.</summary>
    [JsonPropertyName("empresa")]
    public string? Empresa { get; set; }

    /// <summary>Ubicación / ciudad de la vacante.</summary>
    [JsonPropertyName("ubicacion")]
    public string? Ubicacion { get; set; }

    /// <summary>Salario en texto (si la vacante lo publica).</summary>
    [JsonPropertyName("salario")]
    public string? Salario { get; set; }

    /// <summary>Fecha de publicación (tal como viene en el JSON).</summary>
    [JsonPropertyName("fechaPublicacion")]
    public string? FechaPublicacion { get; set; }

    /// <summary>Descripción o resumen de la vacante (si viene en la respuesta).</summary>
    [JsonPropertyName("descripcion")]
    public string? Descripcion { get; set; }

    /// <summary>URL pública navegable para abrir la vacante en el navegador.</summary>
    [JsonPropertyName("urlPublica")]
    public string? UrlPublica { get; set; }
}
