namespace OccScraper.Services;

/// <summary>
/// Constantes centralizadas de OCC: endpoints, URLs base y patrones de ruta.
/// Evita tener cadenas mágicas dispersas por el código.
/// </summary>
public static class OccConstants
{
    /// <summary>Host público del sitio web de OCC.</summary>
    public const string SitioBase = "https://www.occ.com.mx";

    /// <summary>Host del servicio que devuelve el detalle JSON de una vacante.</summary>
    public const string DetalleHost = "https://oferta.occ.com.mx";

    /// <summary>
    /// Patrón del endpoint de detalle de una vacante (devuelve JSON completo).
    /// {0} = id de la oferta. Ej: https://oferta.occ.com.mx/offer/21217053/d/j
    /// Lo dispara la propia página al seleccionar una tarjeta; lo interceptamos.
    /// </summary>
    public const string PatronDetalle = DetalleHost + "/offer/{0}/d/j";

    /// <summary>
    /// Expresión regular para extraer el id de oferta de la URL del endpoint de
    /// detalle interceptado. Ej: /offer/21217053/d/j -> 21217053.
    /// </summary>
    public const string RegexIdDetalle = @"/offer/(\d+)/d/j";

    /// <summary>Selector de las tarjetas de vacante en la página de resultados.</summary>
    public const string SelectorTarjeta = "[data-offers-grid-offer-item-container]";

    /// <summary>
    /// Patrón de la URL pública de resultados de búsqueda.
    /// {0} = empleo (slug), {1} = ciudad (slug).
    /// Ej: https://www.occ.com.mx/empleos/de-analista/en-ciudad-de-mexico/
    /// </summary>
    public const string PatronUrlResultados = SitioBase + "/empleos/de-{0}/en-{1}/";

    /// <summary>
    /// Patrón de la URL pública navegable de una vacante individual.
    /// {0} = empleo (slug), {1} = ciudad (slug), {2} = jobid.
    /// Ej: https://www.occ.com.mx/empleos/de-analista/en-ciudad-de-mexico/?jobid=12345678
    /// </summary>
    public const string PatronUrlVacante = SitioBase + "/empleos/de-{0}/en-{1}/?jobid={2}";

    /// <summary>
    /// Parámetro de consulta para la paginación del endpoint (número de página).
    /// Preparado para uso futuro; por defecto solo se captura la primera página.
    /// </summary>
    public const string ParametroPagina = "pn";
}
