namespace OccScraper.Services;

/// <summary>
/// Constantes centralizadas de Computrabajo México: URLs base, patrones y selectores.
/// </summary>
public static class ComputrabajoConstants
{
    /// <summary>Host público del sitio.</summary>
    public const string SitioBase = "https://mx.computrabajo.com";

    /// <summary>
    /// Patrón de la URL de resultados de búsqueda.
    /// {0} = empleo (slug), {1} = ciudad (slug).
    /// Ej: https://mx.computrabajo.com/trabajo-de-analista-en-ciudad-de-mexico
    /// </summary>
    public const string PatronUrlResultados = SitioBase + "/trabajo-de-{0}-en-{1}";

    // --- Selectores de la página de resultados (SSR) ------------------------------
    // El sitio renderiza cada vacante en el servidor dentro de un <article class="box_offer">.

    /// <summary>Contenedor de cada tarjeta de vacante.</summary>
    public const string SelectorTarjeta = "article.box_offer";

    /// <summary>Título + enlace de la vacante (dentro de la tarjeta).</summary>
    public const string SelectorTitulo = "h2 a.js-o-link";

    /// <summary>Nombre + enlace de la empresa (dentro de la tarjeta).</summary>
    public const string SelectorEmpresa = "a[offer-grid-article-company-url]";

    /// <summary>
    /// Ubicación de la vacante (dentro de la tarjeta). Se excluye el &lt;p class="dFlex"&gt;
    /// de la empresa, que también es fs16 y contiene un span.mr10 (la calificación con estrella).
    /// </summary>
    public const string SelectorUbicacion = "p.fs16:not(.dFlex) span.mr10";

    /// <summary>Salario, cuando existe (dentro de la tarjeta).</summary>
    public const string SelectorSalario = "span.i_salary";

    /// <summary>Fecha de publicación relativa, ej. "Hace 2 horas" (dentro de la tarjeta).</summary>
    public const string SelectorFecha = "p.fc_aux";

    // --- Selectores de la página de detalle de una vacante ------------------------

    /// <summary>
    /// Contenedor completo de la descripción en la página de detalle. Incluye el texto
    /// del puesto, educación, conocimientos, etc. (div con atributo div-link="oferta").
    /// </summary>
    public const string SelectorDescripcionDetalle = "[div-link=\"oferta\"]";
}
