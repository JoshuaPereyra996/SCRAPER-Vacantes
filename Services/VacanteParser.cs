using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using OccScraper.Models;

namespace OccScraper.Services;

/// <summary>
/// Convierte el JSON crudo (array de detalles de vacante de oferta.occ.com.mx)
/// en una lista limpia de Vacante.
///
/// Cada elemento del array tiene la forma { "o": {...}, "c": {...}, ... } donde la
/// sección "o" (oferta) trae los campos relevantes con nombres abreviados:
///   eoi = id, t/ltr = título, cn = empresa, c = municipio, l = ciudad/estado,
///   lss = salario formateado, dluf = fecha (texto), dlur = fecha relativa,
///   ld = descripción (HTML), ur = URL relativa canónica, cat = categoría.
/// </summary>
public static class VacanteParser
{
    /// <summary>
    /// Parsea el JSON crudo y devuelve la lista de vacantes limpias.
    /// </summary>
    /// <param name="jsonCrudo">Array JSON con el detalle de cada vacante.</param>
    /// <param name="empleo">Slug de empleo (respaldo para la URL pública).</param>
    /// <param name="ciudad">Slug de ciudad (respaldo para la URL pública).</param>
    public static List<Vacante> Parsear(string jsonCrudo, string empleo, string ciudad)
    {
        var vacantes = new List<Vacante>();

        if (string.IsNullOrWhiteSpace(jsonCrudo))
        {
            Console.Error.WriteLine("[parser] El JSON crudo está vacío; no hay nada que parsear.");
            return vacantes;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(jsonCrudo);
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"[parser] El JSON crudo no se pudo parsear: {ex.Message}");
            return vacantes;
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                Console.Error.WriteLine("[parser] Se esperaba un array de detalles de vacante.");
                return vacantes;
            }

            foreach (var elemento in doc.RootElement.EnumerateArray())
            {
                // La sección "o" contiene los datos de la oferta.
                if (!elemento.TryGetProperty("o", out var o) || o.ValueKind != JsonValueKind.Object)
                    continue;

                var jobId = ObtenerCadena(o, "eoi");

                var vacante = new Vacante
                {
                    JobId = jobId,
                    Titulo = ObtenerCadena(o, "t") ?? ObtenerCadena(o, "ltr") ?? ObtenerCadena(o, "lc"),
                    Empresa = ObtenerCadena(o, "cn"),
                    Ubicacion = CombinarUbicacion(ObtenerCadena(o, "c"), ObtenerCadena(o, "l")),
                    Salario = ObtenerCadena(o, "lss"),
                    FechaPublicacion = ObtenerCadena(o, "dluf") ?? ObtenerCadena(o, "dlu"),
                    Descripcion = LimpiarHtml(ObtenerCadena(o, "ld")),
                    UrlPublica = ConstruirUrlPublica(ObtenerCadena(o, "ur"), empleo, ciudad, jobId)
                };

                vacantes.Add(vacante);
            }
        }

        Console.WriteLine($"[parser] Vacantes extraídas: {vacantes.Count}");
        return vacantes;
    }

    /// <summary>Lee una propiedad como cadena (acepta string o número).</summary>
    private static string? ObtenerCadena(JsonElement obj, string llave)
    {
        if (!obj.TryGetProperty(llave, out var valor))
            return null;

        return valor.ValueKind switch
        {
            JsonValueKind.String => string.IsNullOrWhiteSpace(valor.GetString()) ? null : valor.GetString()!.Trim(),
            JsonValueKind.Number => valor.GetRawText(),
            _ => null
        };
    }

    /// <summary>Combina municipio y ciudad/estado en una sola cadena legible.</summary>
    private static string? CombinarUbicacion(string? municipio, string? ciudad)
    {
        var partes = new[] { municipio, ciudad }
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToArray();
        return partes.Length == 0 ? null : string.Join(", ", partes);
    }

    /// <summary>Quita etiquetas HTML y decodifica entidades de la descripción.</summary>
    private static string? LimpiarHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return null;

        // Sustituir saltos/listas por espacios y eliminar etiquetas.
        var sinSaltos = Regex.Replace(html, "<(br|/p|/li|/div)[^>]*>", " ", RegexOptions.IgnoreCase);
        var sinTags = Regex.Replace(sinSaltos, "<[^>]+>", string.Empty);
        var decodificado = WebUtility.HtmlDecode(sinTags);
        // Compactar espacios múltiples.
        return Regex.Replace(decodificado, "\\s+", " ").Trim();
    }

    /// <summary>
    /// Construye la URL pública navegable. Prefiere la URL canónica (campo "ur");
    /// si no hay, arma la URL con el patrón estándar y el jobid.
    /// </summary>
    private static string? ConstruirUrlPublica(string? urlCanonica, string empleo, string ciudad, string? jobId)
    {
        if (!string.IsNullOrWhiteSpace(urlCanonica))
        {
            if (urlCanonica.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                return urlCanonica;
            return OccConstants.SitioBase + (urlCanonica.StartsWith('/') ? urlCanonica : "/" + urlCanonica);
        }

        if (!string.IsNullOrWhiteSpace(jobId))
            return string.Format(OccConstants.PatronUrlVacante, empleo, ciudad, jobId);

        return null;
    }
}
