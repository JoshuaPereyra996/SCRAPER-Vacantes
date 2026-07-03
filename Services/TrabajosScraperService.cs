using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using OccScraper.Models;

namespace OccScraper.Services;

/// <summary>
/// Scraper de Trabajos.mx (implementa <see cref="ISitioScraper"/>).
///
/// La página de resultados está renderizada en el servidor (SSR), pero para no
/// depender de clases CSS frágiles la extracción se hace por PATRONES DE URL:
/// cada oferta se reconoce por su enlace /bolsa-trabajo/{id}/{slug}/ y la empresa
/// por su enlace /empresa/. La fecha (dd/mm/aaaa) y el salario ($ ...) se extraen
/// por regex del texto de la tarjeta.
///
/// La descripción completa se obtiene visitando la página de cada oferta:
/// primero se intenta el JSON-LD (schema.org/JobPosting), luego selectores
/// conocidos, y como último recurso el bloque de texto más largo de la página.
/// </summary>
public class TrabajosScraperService : ISitioScraper
{
    private readonly OpcionesScraper _opciones;

    public TrabajosScraperService(OpcionesScraper opciones)
    {
        _opciones = opciones;
    }

    public string Nombre => "trabajos";

    public async Task<ResultadoScrape?> BuscarAsync(string empleo, string ciudad)
    {
        var depurar = Environment.GetEnvironmentVariable("DEBUG_RESPONSES") == "1";

        using var playwright = await Playwright.CreateAsync();
        await using var navegador = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = _opciones.Headless
        });
        var contexto = await navegador.NewContextAsync(new BrowserNewContextOptions
        {
            UserAgent = _opciones.UserAgent
        });
        var pagina = await contexto.NewPageAsync();

        // --- 1. Navegar a la página de resultados -------------------------------
        var urlResultados = string.Format(TrabajosConstants.PatronUrlResultados, ciudad, empleo);
        Console.WriteLine($"[scraper] Navegando a: {urlResultados}");

        try
        {
            await pagina.GotoAsync(urlResultados, new PageGotoOptions
            {
                Timeout = _opciones.TimeoutMs,
                WaitUntil = WaitUntilState.DOMContentLoaded
            });
        }
        catch (TimeoutException)
        {
            Console.Error.WriteLine("[scraper] La navegación agotó el tiempo de espera.");
        }
        catch (PlaywrightException ex)
        {
            Console.Error.WriteLine($"[scraper] Error de Playwright durante la navegación: {ex.Message}");
            return null;
        }

        await Task.Delay(_opciones.DelayMs);

        var htmlResultados = await pagina.ContentAsync();

        // --- 2. Extraer las tarjetas por patrón de URL (robusto ante cambios CSS) ---
        var jsonTarjetas = await pagina.EvaluateAsync<string>(ScriptExtraerTarjetas);
        var vacantes = ParsearTarjetas(jsonTarjetas);
        Console.WriteLine($"[scraper] Vacantes encontradas en la página: {vacantes.Count}");

        if (vacantes.Count == 0)
        {
            Console.Error.WriteLine("[scraper] No se encontraron ofertas (¿cambió el HTML o el patrón de URL?).");
            return null;
        }

        // --- 3. Visitar cada oferta para obtener la descripción completa --------
        for (var i = 0; i < vacantes.Count; i++)
        {
            var v = vacantes[i];
            if (string.IsNullOrWhiteSpace(v.UrlPublica))
                continue;

            try
            {
                await pagina.GotoAsync(v.UrlPublica, new PageGotoOptions
                {
                    Timeout = _opciones.TimeoutMs,
                    WaitUntil = WaitUntilState.DOMContentLoaded
                });
                await Task.Delay(500);

                if (depurar && i == 0)
                {
                    var hd = Path.Combine(AppContext.BaseDirectory, "output", "_debug_detalle_trabajos.html");
                    Directory.CreateDirectory(Path.GetDirectoryName(hd)!);
                    await File.WriteAllTextAsync(hd, await pagina.ContentAsync());
                    Console.WriteLine($"[debug] HTML de detalle guardado en {hd}");
                }

                var desc = await pagina.EvaluateAsync<string?>(ScriptExtraerDescripcion);
                v.Descripcion = LimpiarHtml(desc);
                if (depurar) Console.WriteLine($"[debug] detalle {i + 1}/{vacantes.Count}: " +
                    (v.Descripcion is null ? "sin descripción" : v.Descripcion.Length + " chars"));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[scraper] No se pudo abrir el detalle de '{v.Titulo}': {ex.Message}");
            }

            // Espera corta entre páginas para no martillar el servidor.
            await Task.Delay(400);
        }

        Console.WriteLine($"[scraper] Vacantes procesadas: {vacantes.Count}");
        return new ResultadoScrape(htmlResultados, "html", vacantes);
    }

    // ------------------------------------------------------------------------
    //  Scripts de extracción (se ejecutan en el contexto de la página)
    // ------------------------------------------------------------------------

    /// <summary>
    /// Recolecta las ofertas del listado: agrupa los enlaces /bolsa-trabajo/{id}/
    /// por id (quedándose con el de texto más largo = el título), sube al contenedor
    /// de la tarjeta y extrae empresa, ubicación, fecha y salario.
    /// Devuelve un JSON string para deserializar en C#.
    /// </summary>
    private const string ScriptExtraerTarjetas = @"
() => {
    const reId = /\/bolsa-trabajo\/(\d+)\//;
    const porId = new Map();

    // 1) Mejor enlace (título) por oferta.
    for (const a of document.querySelectorAll('a[href]')) {
        const m = a.href.match(reId);
        if (!m) continue;
        const txt = (a.textContent || '').trim();
        const previo = porId.get(m[1]);
        if (!previo || txt.length > previo.titulo.length)
            porId.set(m[1], { id: m[1], url: a.href.split('?')[0], titulo: txt, ancla: a });
    }

    // 2) Datos del contenedor de cada tarjeta.
    const resultado = [];
    for (const o of porId.values()) {
        if (!o.titulo || o.titulo.length < 3) continue;   // enlaces de icono/imagen
        let c = o.ancla.parentElement;
        for (let i = 0; i < 7 && c; i++) {
            if (c.querySelector('a[href*=""/empresa/""]')) break;
            c = c.parentElement;
        }
        const cont = c || o.ancla.parentElement || document.body;
        const emp = cont.querySelector('a[href*=""/empresa/""]');
        const strong = cont.querySelector('strong, b');
        const texto = (cont.innerText || '').replace(/\s+/g, ' ');
        const fecha = (texto.match(/\d{2}\/\d{2}\/\d{4}/) || [null])[0];
        const salario = (texto.match(/\$\s?[\d.,]+(\s?-\s?\$?\s?[\d.,]+)?/) || [null])[0];
        resultado.push({
            id: o.id,
            url: o.url,
            titulo: o.titulo,
            empresa: emp ? (emp.textContent || '').trim() : null,
            ubicacion: strong ? (strong.textContent || '').trim() : null,
            fecha: fecha,
            salario: salario
        });
    }
    return JSON.stringify(resultado);
}";

    /// <summary>
    /// Extrae la descripción en la página de detalle, en orden de robustez:
    /// 1) JSON-LD schema.org/JobPosting (campo description),
    /// 2) selectores conocidos,
    /// 3) el bloque de texto más largo de la página (último recurso).
    /// </summary>
    private const string ScriptExtraerDescripcion = @"
() => {
    // 1) JSON-LD
    for (const s of document.querySelectorAll('script[type=""application/ld+json""]')) {
        try {
            const d = JSON.parse(s.textContent);
            for (const o of (Array.isArray(d) ? d : [d])) {
                if (o && o['@type'] === 'JobPosting' && o.description)
                    return o.description;
            }
        } catch (e) { }
    }
    // 2) Selectores conocidos
    const sel = document.querySelector(
        '[itemprop=\'description\'], .descripcion, #descripcion, .detalle-oferta');
    if (sel && sel.innerText && sel.innerText.trim().length > 40)
        return sel.innerText;
    // 3) Bloque de texto más largo (excluyendo scripts/nav/footer)
    let mejor = null, mejorLen = 0;
    for (const el of document.querySelectorAll('div, section, article, td')) {
        if (el.closest('nav, footer, header, script, style')) continue;
        if (el.querySelector('div, section, article')) continue;   // solo hojas
        const t = (el.innerText || '').trim();
        if (t.length > mejorLen) { mejorLen = t.length; mejor = t; }
    }
    return mejorLen > 80 ? mejor : null;
}";

    // ------------------------------------------------------------------------
    //  Parseo del JSON de tarjetas a Vacante
    // ------------------------------------------------------------------------

    private List<Vacante> ParsearTarjetas(string? json)
    {
        var vacantes = new List<Vacante>();
        if (string.IsNullOrWhiteSpace(json))
            return vacantes;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"[parser] JSON de tarjetas inválido: {ex.Message}");
            return vacantes;
        }

        using (doc)
        {
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                vacantes.Add(new Vacante
                {
                    Fuente = Nombre,
                    JobId = Cadena(e, "id"),
                    Titulo = Cadena(e, "titulo"),
                    Empresa = Cadena(e, "empresa"),
                    Ubicacion = LimpiarUbicacion(Cadena(e, "ubicacion")),
                    Salario = Cadena(e, "salario"),
                    FechaPublicacion = Cadena(e, "fecha"),
                    UrlPublica = Cadena(e, "url")
                });
            }
        }
        return vacantes;
    }

    private static string? Cadena(JsonElement e, string llave)
        => e.TryGetProperty(llave, out var v) && v.ValueKind == JsonValueKind.String
           && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString()!.Trim()
            : null;

    /// <summary>Quita el prefijo "Todo " que usa el sitio ("Todo Ciudad De México").</summary>
    private static string? LimpiarUbicacion(string? ubicacion)
    {
        if (string.IsNullOrWhiteSpace(ubicacion))
            return null;
        return Regex.Replace(ubicacion, @"^Todo\s+", "", RegexOptions.IgnoreCase).Trim();
    }

    /// <summary>Quita etiquetas HTML, decodifica entidades y compacta espacios.</summary>
    private static string? LimpiarHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return null;
        var sinSaltos = Regex.Replace(html, "<(br|/p|/li|/div)[^>]*>", " ", RegexOptions.IgnoreCase);
        var sinTags = Regex.Replace(sinSaltos, "<[^>]+>", string.Empty);
        var decodificado = WebUtility.HtmlDecode(sinTags);
        return Regex.Replace(decodificado, @"\s+", " ").Trim();
    }
}
