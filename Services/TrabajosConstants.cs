namespace OccScraper.Services;

/// <summary>
/// Constantes centralizadas de Trabajos.mx (Trabajos.com México / Hispavista):
/// URLs base, patrones y expresiones regulares.
///
/// robots.txt verificado (2026-07): permite leer resultados y fichas de oferta;
/// solo bloquea /buscar-avanzado/ y banners. Scraping de bajo volumen OK.
/// </summary>
public static class TrabajosConstants
{
    /// <summary>Host público del sitio.</summary>
    public const string SitioBase = "https://www.trabajos.mx";

    /// <summary>
    /// Patrón de la URL de resultados de búsqueda.
    /// {0} = ciudad/ESTADO (slug), {1} = empleo (slug).
    /// OJO: el sitio filtra por ESTADO, no por ciudad. Slugs válidos: ciudad-de-mexico,
    /// estado-mexico, jalisco, nuevo-leon, puebla, queretaro, etc.
    /// Ej: https://www.trabajos.mx/bolsa-trabajo/ciudad-de-mexico/analista
    /// </summary>
    public const string PatronUrlResultados = SitioBase + "/bolsa-trabajo/{0}/{1}";

    /// <summary>
    /// Expresión regular para reconocer la URL de una oferta individual y extraer su id.
    /// Ej: /bolsa-trabajo/1196535575/analista-de-atraccion-de-talento/ -> 1196535575
    /// </summary>
    public const string RegexIdOferta = @"/bolsa-trabajo/(\d+)/";

    /// <summary>Fragmento de URL que identifica el enlace a la página de una empresa.</summary>
    public const string FragmentoUrlEmpresa = "/empresa/";

    /// <summary>
    /// Selector de posibles contenedores de la descripción en la página de detalle
    /// (se intentan en orden; si ninguno existe se usa el JSON-LD o el bloque más largo).
    /// </summary>
    public const string SelectorDescripcionDetalle =
        "[itemprop='description'], .descripcion, #descripcion, .detalle-oferta";
}
