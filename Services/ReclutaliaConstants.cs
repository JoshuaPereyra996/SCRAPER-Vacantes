namespace OccScraper.Services;

/// <summary>
/// Constantes centralizadas de Reclutalia (reclutalia.com): host de la API JSON,
/// endpoint de búsqueda, parámetros fijos y patrón de la URL pública.
///
/// A diferencia de los demás sitios, Reclutalia es un SPA de Next.js que carga las
/// vacantes desde una API REST pública (sin autenticación). Por eso este scraper NO
/// usa Playwright: hace un simple GET HTTP y parsea el JSON. El sitio filtra la
/// ubicación por geocoordenadas (Google Places), no por un slug de ciudad, así que
/// el filtrado por ciudad se hace del lado del cliente sobre la dirección de cada
/// vacante (ver <c>ReclutaliaScraperService</c>).
/// </summary>
public static class ReclutaliaConstants
{
    /// <summary>Host público del sitio (para armar la URL navegable de cada vacante).</summary>
    public const string SitioBase = "https://reclutalia.com";

    /// <summary>Host de la API REST que sirve las vacantes.</summary>
    public const string ApiBase = "https://api.reclutalia.com";

    /// <summary>
    /// Patrón del endpoint de búsqueda. {0} = término (tags, ya URL-encodeado),
    /// {1} = límite de resultados. Los parámetros channel/referer/env son fijos y
    /// obligatorios (sin channel válido la API responde 400).
    /// Ej: https://api.reclutalia.com/job-offers/search?tags=analista&amp;offset=0&amp;limit=50&amp;channel=WEB&amp;referer=WEB&amp;env=production
    /// </summary>
    public const string PatronUrlBusqueda =
        ApiBase + "/job-offers/search?tags={0}&offset=0&limit={1}&channel=WEB&referer=WEB&env=production";

    /// <summary>
    /// Patrón de la URL pública navegable de una vacante. {0} = code de la vacante.
    /// Ej: https://reclutalia.com/job-offers/?jobOffer=3OOA278HB8
    /// </summary>
    public const string PatronUrlPublica = SitioBase + "/job-offers/?jobOffer={0}";

    /// <summary>
    /// Cuántas vacantes pedir en la única página de resultados. La API pagina de 6 en
    /// 6 por defecto, pero acepta límites mayores; pedimos un lote amplio para que el
    /// filtrado por ciudad (cliente) tenga material suficiente.
    /// </summary>
    public const int LimiteResultados = 50;
}
