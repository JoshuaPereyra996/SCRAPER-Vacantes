using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace OccScraper.Services;

/// <summary>
/// Opciones de ejecución del scraper (mapeadas desde appsettings.json).
/// </summary>
public record OccScraperOptions(
    bool Headless,
    int TimeoutMs,
    int DelayMs,
    string UserAgent);

/// <summary>
/// Servicio que controla un Chromium real con Playwright.
///
/// Flujo descubierto en OCC:
///  1) La página de resultados (SSR) incrusta en su HTML la lista de ids de oferta
///     ("OfferId":"...") pero NO el contenido de cada vacante.
///  2) El contenido completo de cada vacante se obtiene del endpoint de detalle
///     https://oferta.occ.com.mx/offer/{id}/d/j (JSON), que requiere la sesión real
///     del navegador (cookies). El POST a api-collector.../offer/search es solo
///     telemetría (responde "OK"), no datos.
///
/// Por eso: cargamos la página, extraemos los ids y pedimos el detalle de cada uno
/// reutilizando la sesión del navegador (APIRequest comparte las cookies del contexto).
/// </summary>
public class OccScraperService
{
    private readonly OccScraperOptions _opciones;

    public OccScraperService(OccScraperOptions opciones)
    {
        _opciones = opciones;
    }

    /// <summary>
    /// Realiza UNA búsqueda y devuelve un array JSON crudo con el detalle de cada vacante.
    /// </summary>
    /// <param name="empleo">Palabra clave del puesto (slug).</param>
    /// <param name="ciudad">Ciudad (slug).</param>
    /// <returns>JSON crudo (array de detalles), o null si no se obtuvo nada.</returns>
    public async Task<string?> BuscarAsync(string empleo, string ciudad)
    {
        var depurar = Environment.GetEnvironmentVariable("DEBUG_RESPONSES") == "1";

        // Inicializar Playwright y lanzar Chromium.
        using var playwright = await Playwright.CreateAsync();
        await using var navegador = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = _opciones.Headless
        });

        // Contexto con User-Agent realista; sus cookies se comparten con APIRequest.
        var contexto = await navegador.NewContextAsync(new BrowserNewContextOptions
        {
            UserAgent = _opciones.UserAgent
        });

        var pagina = await contexto.NewPageAsync();

        // --- 1. Navegar a la página pública de resultados -----------------------
        var urlResultados = string.Format(OccConstants.PatronUrlResultados, empleo, ciudad);
        Console.WriteLine($"[scraper] Navegando a: {urlResultados}");

        try
        {
            // DOMContentLoaded: NetworkIdle no se cumple por la analítica constante del sitio.
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

        // Espera breve para que el sitio hidrate y renderice las tarjetas.
        await Task.Delay(_opciones.DelayMs);

        // --- 2. Interceptar las respuestas de detalle que dispara la página -----
        // Al seleccionar una tarjeta, el sitio pide oferta.occ.com.mx/offer/{id}/d/j
        // con la sesión y cabeceras correctas; aquí capturamos ese JSON rico.
        var detallesPorId = new Dictionary<string, string>();
        var regexIdDetalle = new Regex(OccConstants.RegexIdDetalle);

        pagina.Response += async (_, respuesta) =>
        {
            var m = regexIdDetalle.Match(respuesta.Url);
            if (!m.Success)
                return;
            try
            {
                var cuerpo = await respuesta.TextAsync();
                if (!string.IsNullOrWhiteSpace(cuerpo) && cuerpo.TrimStart().StartsWith('{'))
                {
                    detallesPorId[m.Groups[1].Value] = cuerpo;
                    if (depurar) Console.WriteLine($"[debug] detalle {m.Groups[1].Value}: OK ({cuerpo.Length} chars)");
                }
            }
            catch { /* ignorar respuestas no legibles */ }
        };

        // --- 3. Recorrer las tarjetas y hacer clic para cargar cada detalle -----
        var tarjetas = pagina.Locator(OccConstants.SelectorTarjeta);
        var total = await tarjetas.CountAsync();
        Console.WriteLine($"[scraper] Vacantes encontradas en la página: {total}");

        if (total == 0)
        {
            Console.Error.WriteLine("[scraper] No se encontraron tarjetas de vacante en la página.");
            return null;
        }

        for (var i = 0; i < total; i++)
        {
            try
            {
                var tarjeta = tarjetas.Nth(i);
                await tarjeta.ScrollIntoViewIfNeededAsync();
                await tarjeta.ClickAsync(new LocatorClickOptions { Timeout = 8000 });
                // Esperar a que llegue el detalle de esta tarjeta.
                await Task.Delay(700);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[scraper] No se pudo abrir la tarjeta #{i + 1}: {ex.Message}");
            }
        }

        // Espera final para no perder el último detalle en vuelo.
        await Task.Delay(_opciones.DelayMs);

        var detalles = detallesPorId.Values.ToList();
        Console.WriteLine($"[scraper] Detalles obtenidos: {detalles.Count}/{total}");

        if (detalles.Count == 0)
            return null;

        // Combinar todos los detalles en un único array JSON crudo.
        return "[" + string.Join(",", detalles) + "]";
    }
}
