using System.Text.RegularExpressions;
using Microsoft.Playwright;
using OccScraper.Models;

namespace OccScraper.Services;

/// <summary>
/// Scraper de Computrabajo México (implementa <see cref="ISitioScraper"/>).
///
/// A diferencia de OCC, la página de resultados está renderizada en el servidor (SSR):
/// cada vacante viene como un &lt;article class="box_offer"&gt; con título, empresa,
/// ubicación, salario y fecha directamente en el HTML. La descripción completa NO está
/// en la lista; se obtiene visitando la página de cada oferta (que da 403 sin navegador
/// real, por eso usamos Playwright).
/// </summary>
public class ComputrabajoScraperService : ISitioScraper
{
    private readonly OpcionesScraper _opciones;

    public ComputrabajoScraperService(OpcionesScraper opciones)
    {
        _opciones = opciones;
    }

    public string Nombre => "computrabajo";

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
        var urlResultados = string.Format(ComputrabajoConstants.PatronUrlResultados, empleo, ciudad);
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

        // Guardar el HTML de resultados como contenido crudo.
        var htmlResultados = await pagina.ContentAsync();

        // --- 2. Extraer los campos de cada tarjeta ------------------------------
        var tarjetas = pagina.Locator(ComputrabajoConstants.SelectorTarjeta);
        var total = await tarjetas.CountAsync();
        Console.WriteLine($"[scraper] Vacantes encontradas en la página: {total}");

        if (total == 0)
        {
            Console.Error.WriteLine("[scraper] No se encontraron tarjetas de vacante (¿cambió el HTML?).");
            return null;
        }

        var vacantes = new List<Vacante>();
        foreach (var (i, _) in Enumerable.Range(0, total).Select(n => (n, 0)))
        {
            var tarjeta = tarjetas.Nth(i);
            var href = await AtributoSeguroAsync(tarjeta.Locator(ComputrabajoConstants.SelectorTitulo), "href");

            var vacante = new Vacante
            {
                Fuente = Nombre,
                JobId = await AtributoSeguroAsync(tarjeta, "data-id"),
                Titulo = await TextoSeguroAsync(tarjeta.Locator(ComputrabajoConstants.SelectorTitulo)),
                Empresa = await TextoSeguroAsync(tarjeta.Locator(ComputrabajoConstants.SelectorEmpresa)),
                Ubicacion = await TextoSeguroAsync(tarjeta.Locator(ComputrabajoConstants.SelectorUbicacion)),
                Salario = LimpiarEspacios(await TextoSeguroAsync(SalarioLocator(tarjeta))),
                FechaPublicacion = LimpiarEspacios(await TextoSeguroAsync(tarjeta.Locator(ComputrabajoConstants.SelectorFecha))),
                UrlPublica = ConstruirUrl(href)
            };

            vacantes.Add(vacante);
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
                    var hd = Path.Combine(AppContext.BaseDirectory, "output", "_debug_detalle_ct.html");
                    Directory.CreateDirectory(Path.GetDirectoryName(hd)!);
                    await File.WriteAllTextAsync(hd, await pagina.ContentAsync());
                    Console.WriteLine($"[debug] HTML de detalle guardado en {hd}");
                }

                var desc = await TextoSeguroAsync(pagina.Locator(ComputrabajoConstants.SelectorDescripcionDetalle).First);
                v.Descripcion = LimpiarEspacios(desc);
                if (depurar) Console.WriteLine($"[debug] detalle {i + 1}/{vacantes.Count}: {(desc is null ? "sin descripción" : desc.Length + " chars")}");
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

    /// <summary>Localiza el span que contiene el texto del salario (a partir del icono).</summary>
    private static ILocator SalarioLocator(ILocator tarjeta)
        => tarjeta.Locator(ComputrabajoConstants.SelectorSalario).Locator("xpath=..");

    /// <summary>Lee el texto del primer match de un locator; null si no hay ninguno.</summary>
    private static async Task<string?> TextoSeguroAsync(ILocator loc)
    {
        try
        {
            if (await loc.CountAsync() == 0)
                return null;
            // .First evita el error de "modo estricto" si el selector matchea varios.
            var t = await loc.First.InnerTextAsync();
            return string.IsNullOrWhiteSpace(t) ? null : t.Trim();
        }
        catch { return null; }
    }

    /// <summary>Lee un atributo del primer match de un locator; null si no hay ninguno.</summary>
    private static async Task<string?> AtributoSeguroAsync(ILocator loc, string atributo)
    {
        try
        {
            if (await loc.CountAsync() == 0)
                return null;
            return await loc.First.GetAttributeAsync(atributo);
        }
        catch { return null; }
    }

    /// <summary>Convierte una URL relativa en absoluta usando el host del sitio.</summary>
    private static string? ConstruirUrl(string? href)
    {
        if (string.IsNullOrWhiteSpace(href))
            return null;
        if (href.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return href;
        return ComputrabajoConstants.SitioBase + (href.StartsWith('/') ? href : "/" + href);
    }

    /// <summary>Compacta espacios/saltos múltiples en uno solo.</summary>
    private static string? LimpiarEspacios(string? texto)
        => string.IsNullOrWhiteSpace(texto) ? null : Regex.Replace(texto, "\\s+", " ").Trim();
}
