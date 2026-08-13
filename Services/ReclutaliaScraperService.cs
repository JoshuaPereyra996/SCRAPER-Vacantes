using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OccScraper.Models;

namespace OccScraper.Services;

/// <summary>
/// Scraper de Reclutalia (reclutalia.com), implementa <see cref="ISitioScraper"/>.
///
/// Reclutalia es un SPA de Next.js que carga las vacantes desde una API REST pública
/// (<c>api.reclutalia.com/job-offers/search</c>) sin autenticación. Por eso, a
/// diferencia de OCC/Computrabajo/Trabajos, este scraper NO usa Playwright: hace un
/// único GET HTTP y parsea el JSON. La descripción ya viene completa en la respuesta
/// de búsqueda (el endpoint de detalle devuelve la misma), así que no se visita cada
/// vacante.
///
/// La búsqueda de la API es por palabra clave (<c>tags</c>); la ubicación se filtra
/// por geocoordenadas (Google Places), no por un slug de ciudad. Como aquí solo
/// tenemos el nombre de la ciudad, el filtrado por ciudad se hace del lado del cliente
/// comparando contra la dirección de cada vacante.
/// </summary>
public class ReclutaliaScraperService : ISitioScraper
{
    private readonly OpcionesScraper _opciones;

    public ReclutaliaScraperService(OpcionesScraper opciones)
    {
        _opciones = opciones;
    }

    public string Nombre => "reclutalia";

    public async Task<ResultadoScrape?> BuscarAsync(string empleo, string ciudad)
    {
        var depurar = Environment.GetEnvironmentVariable("DEBUG_RESPONSES") == "1";

        // El endpoint 'tags' es de palabra clave: los términos multi-palabra llegan
        // como slug ("auxiliar-contable"); se pasan a espacios para una consulta natural.
        var termino = (empleo ?? string.Empty).Replace('-', ' ').Trim();
        var url = string.Format(
            ReclutaliaConstants.PatronUrlBusqueda,
            Uri.EscapeDataString(termino),
            ReclutaliaConstants.LimiteResultados);
        Console.WriteLine($"[scraper] Consultando API: {url}");

        // --- 1. Petición HTTP a la API (sin navegador) --------------------------
        using var http = new HttpClient
        {
            Timeout = TimeSpan.FromMilliseconds(_opciones.TimeoutMs)
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(_opciones.UserAgent);
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json");

        string json;
        try
        {
            var respuesta = await http.GetAsync(url);
            json = await respuesta.Content.ReadAsStringAsync();
            if (respuesta.StatusCode != HttpStatusCode.OK)
            {
                Console.Error.WriteLine($"[scraper] La API respondió {(int)respuesta.StatusCode}. Cuerpo: {Recortar(json)}");
                return null;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[scraper] Error consultando la API: {ex.Message}");
            return null;
        }

        // --- 2. Parsear el JSON a Vacante ---------------------------------------
        var vacantes = Parsear(json);
        Console.WriteLine($"[scraper] Vacantes devueltas por la API: {vacantes.Count}");

        if (vacantes.Count == 0)
        {
            Console.Error.WriteLine("[scraper] La API no devolvió vacantes para ese término (¿sin resultados o cambió el JSON?).");
            return null;
        }

        // --- 3. Filtrar por ciudad (cliente) ------------------------------------
        var ciudadNorm = Normalizar(ciudad);
        if (!string.IsNullOrWhiteSpace(ciudadNorm))
        {
            var enCiudad = vacantes
                .Where(v => Normalizar(v.Ubicacion).Contains(ciudadNorm, StringComparison.Ordinal))
                .ToList();

            Console.WriteLine($"[scraper] Vacantes que coinciden con '{ciudad}': {enCiudad.Count} de {vacantes.Count}.");
            if (enCiudad.Count > 0)
            {
                vacantes = enCiudad;
            }
            else
            {
                // Reclutalia tiene inventario limitado por ciudad; si nada coincide se
                // devuelven todas las del término (con su ubicación real) en vez de nada.
                Console.WriteLine("[scraper] Ninguna coincide con la ciudad; se devuelven todas las del término.");
            }
        }

        if (depurar)
            foreach (var v in vacantes)
                Console.WriteLine($"[debug] {v.Titulo} | {v.Empresa} | {v.Ubicacion} | {v.Salario}");

        Console.WriteLine($"[scraper] Vacantes procesadas: {vacantes.Count}");
        return new ResultadoScrape(json, "json", vacantes);
    }

    // ------------------------------------------------------------------------
    //  Parseo del JSON de la API a Vacante
    // ------------------------------------------------------------------------

    private List<Vacante> Parsear(string json)
    {
        var vacantes = new List<Vacante>();

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"[parser] JSON de la API inválido: {ex.Message}");
            return vacantes;
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Object
                || !data.TryGetProperty("jobOffer", out var ofertas)
                || ofertas.ValueKind != JsonValueKind.Array)
            {
                return vacantes;
            }

            foreach (var o in ofertas.EnumerateArray())
            {
                var about = Objeto(o, "about");
                var company = Objeto(o, "company");
                var places = Objeto(o, "places");
                var address = places is { } p ? Objeto(p, "address") : null;
                var code = Cadena(o, "code");

                vacantes.Add(new Vacante
                {
                    Fuente = Nombre,
                    JobId = Cadena(o, "jobId") ?? code,
                    Titulo = about is { } a ? Cadena(a, "title") : null,
                    Empresa = company is { } c ? Cadena(c, "tradeName") : null,
                    Ubicacion = Ubicacion(address),
                    Salario = about is { } a2 ? FormatearSalario(a2) : null,
                    FechaPublicacion = Cadena(o, "publicationDate") ?? Cadena(o, "creationDate"),
                    Descripcion = about is { } a3 ? LimpiarTexto(Cadena(a3, "description")) : null,
                    UrlPublica = string.IsNullOrWhiteSpace(code)
                        ? null
                        : string.Format(ReclutaliaConstants.PatronUrlPublica, code)
                });
            }
        }
        return vacantes;
    }

    /// <summary>Arma la ubicación a partir de los campos de la dirección.</summary>
    private static string? Ubicacion(JsonElement? address)
    {
        if (address is not { } a)
            return null;

        // La dirección formateada suele venir con espacios de más, ej.
        // "  Jardines En La Montaña, Ciudad De México, Ciudad De México, Mx".
        var formateada = LimpiarTexto(Cadena(a, "formattedAddress"));
        if (!string.IsNullOrWhiteSpace(formateada))
            return formateada;

        // Respaldo: componer con localidad/estado si no hay dirección formateada.
        var partes = new[] { Cadena(a, "locality"), Cadena(a, "state") }
            .Where(s => !string.IsNullOrWhiteSpace(s));
        var compuesta = string.Join(", ", partes);
        return string.IsNullOrWhiteSpace(compuesta) ? null : compuesta;
    }

    /// <summary>
    /// Formatea el salario como los demás sitios, ej. "$25,000 - $28,000 Mensual".
    /// Usa mínimo/máximo si existen; si no, el fijo. Devuelve null si no hay monto.
    /// </summary>
    private static string? FormatearSalario(JsonElement about)
    {
        if (!about.TryGetProperty("salary", out var s) || s.ValueKind != JsonValueKind.Object)
            return null;

        var min = Numero(s, "minimum");
        var max = Numero(s, "maximum");
        var fijo = Numero(s, "salaryFixed");
        var rango = Cadena(s, "salaryRange");   // ej. "Mensual"

        string? monto = null;
        if (min > 0 && max > 0 && max != min)
            monto = $"${Miles(min)} - ${Miles(max)}";
        else if (max > 0)
            monto = $"${Miles(max)}";
        else if (fijo > 0)
            monto = $"${Miles(fijo)}";

        if (monto is null)
            return null;

        return string.IsNullOrWhiteSpace(rango) ? monto : $"{monto} {rango}";
    }

    private static string Miles(double n)
        => n.ToString("#,0", CultureInfo.GetCultureInfo("es-MX"));

    // ------------------------------------------------------------------------
    //  Helpers de lectura de JSON
    // ------------------------------------------------------------------------

    private static JsonElement? Objeto(JsonElement e, string llave)
        => e.TryGetProperty(llave, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    private static string? Cadena(JsonElement e, string llave)
        => e.TryGetProperty(llave, out var v) && v.ValueKind == JsonValueKind.String
           && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString()!.Trim()
            : null;

    private static double Numero(JsonElement e, string llave)
        => e.TryGetProperty(llave, out var v) && v.ValueKind == JsonValueKind.Number
           && v.TryGetDouble(out var d)
            ? d
            : 0;

    /// <summary>Decodifica entidades, quita tags HTML y compacta espacios.</summary>
    private static string? LimpiarTexto(string? texto)
    {
        if (string.IsNullOrWhiteSpace(texto))
            return null;
        var sinTags = Regex.Replace(texto, "<[^>]+>", " ");
        var decodificado = WebUtility.HtmlDecode(sinTags);
        return Regex.Replace(decodificado, @"\s+", " ").Trim();
    }

    /// <summary>
    /// Normaliza texto para comparar ciudades sin acentos ni mayúsculas y con
    /// guiones convertidos a espacios (la ciudad llega como slug, ej. "ciudad-de-mexico").
    /// </summary>
    private static string Normalizar(string? texto)
    {
        if (string.IsNullOrWhiteSpace(texto))
            return string.Empty;

        var conEspacios = texto.Replace('-', ' ').ToLowerInvariant();
        var descompuesto = conEspacios.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(descompuesto.Length);
        foreach (var c in descompuesto)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);

        var sinAcentos = sb.ToString().Normalize(NormalizationForm.FormC);
        return Regex.Replace(sinAcentos, @"\s+", " ").Trim();
    }

    private static string Recortar(string? s)
        => string.IsNullOrEmpty(s) ? "" : (s.Length <= 200 ? s : s[..200] + "…");
}
