using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using OccScraper.Services;

// ============================================================================
//  Scraper de vacantes — punto de entrada
//  Flujo: leer config -> elegir sitio -> lanzar Playwright -> obtener vacantes
//         -> guardar contenido crudo -> guardar JSON limpio.
// ============================================================================

// --- 1. Leer configuración (appsettings.json + args de consola) --------------
// Los args sobrescriben el JSON.
// Uso: dotnet run -- --empleo "contador" --ciudad "monterrey" --sitio computrabajo
var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
    .AddCommandLine(args)
    .Build();

var empleo = config["empleo"] ?? config["Busqueda:empleo"];
var ciudad = config["ciudad"] ?? config["Busqueda:ciudad"];
var sitio = (config["sitio"] ?? config["Busqueda:sitio"] ?? "occ").Trim().ToLowerInvariant();

if (string.IsNullOrWhiteSpace(empleo) || string.IsNullOrWhiteSpace(ciudad))
{
    Console.Error.WriteLine("Error: faltan parámetros 'empleo' y/o 'ciudad'.");
    Console.Error.WriteLine("Configúralos en appsettings.json o pásalos por consola:");
    Console.Error.WriteLine("  dotnet run -- --empleo \"analista\" --ciudad \"ciudad-de-mexico\" --sitio occ");
    return 1;
}

// Normalizar a slug simple (minúsculas, sin espacios extremos, espacios -> guiones).
empleo = NormalizarSlug(empleo);
ciudad = NormalizarSlug(ciudad);

// Opciones de Playwright desde la sección "Playwright".
var opciones = new OpcionesScraper(
    Headless: config.GetValue("Playwright:headless", true),
    TimeoutMs: config.GetValue("Playwright:timeoutMs", 60000),
    DelayMs: config.GetValue("Playwright:delayMs", 2000),
    UserAgent: config["Playwright:userAgent"]
        ?? "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");

// --- Selección del sitio -----------------------------------------------------
ISitioScraper scraper;
switch (sitio)
{
    case "occ":
        scraper = new OccScraperService(opciones);
        break;
    case "computrabajo":
        scraper = new ComputrabajoScraperService(opciones);
        break;
    case "trabajos":
        scraper = new TrabajosScraperService(opciones);
        break;
    default:
        Console.Error.WriteLine($"Error: sitio '{sitio}' no soportado. Usa 'occ', 'computrabajo' o 'trabajos'.");
        return 1;
}

Console.WriteLine($"Búsqueda: sitio='{sitio}', empleo='{empleo}', ciudad='{ciudad}' (headless={opciones.Headless})");

// --- 2. Ejecutar el scraper --------------------------------------------------
ResultadoScrape? resultado;
try
{
    resultado = await scraper.BuscarAsync(empleo, ciudad);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Error inesperado ejecutando el scraper: {ex.Message}");
    return 2;
}

if (resultado is null || resultado.Vacantes.Count == 0)
{
    Console.Error.WriteLine("No se obtuvieron vacantes. Nada que guardar.");
    Console.Error.WriteLine("Sugerencia: prueba con headless=false y/o aumenta timeoutMs en appsettings.json.");
    return 3;
}

// --- 3. Preparar carpeta y nombres de salida ---------------------------------
var carpetaSalida = Path.Combine(AppContext.BaseDirectory, "output");
Directory.CreateDirectory(carpetaSalida);

var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
var rutaCruda = Path.Combine(carpetaSalida, $"raw_{sitio}_{empleo}_{ciudad}_{timestamp}.{resultado.ExtensionCrudo}");
var rutaLimpia = Path.Combine(carpetaSalida, $"vacantes_{sitio}_{empleo}_{ciudad}_{timestamp}.json");

// Opciones de serialización: indentado legible y acentos sin escapar.
var jsonOpts = new JsonSerializerOptions
{
    WriteIndented = true,
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
};

// --- 4. Guardar el contenido crudo TAL CUAL (sin modificar) ------------------
try
{
    await File.WriteAllTextAsync(rutaCruda, resultado.Crudo);
    Console.WriteLine($"Contenido crudo guardado en: {rutaCruda}");
}
catch (Exception ex)
{
    Console.Error.WriteLine($"No se pudo guardar el contenido crudo: {ex.Message}");
    return 4;
}

// --- 5. Guardar el JSON limpio ----------------------------------------------
try
{
    var jsonLimpio = JsonSerializer.Serialize(resultado.Vacantes, jsonOpts);
    await File.WriteAllTextAsync(rutaLimpia, jsonLimpio);
    Console.WriteLine($"JSON limpio guardado en: {rutaLimpia} ({resultado.Vacantes.Count} vacantes)");
}
catch (Exception ex)
{
    Console.Error.WriteLine($"No se pudo generar el JSON limpio: {ex.Message}");
    return 5;
}

Console.WriteLine("Listo.");
return 0;

// ============================================================================
//  Funciones locales auxiliares
// ============================================================================

// Convierte un texto a un slug simple compatible con las rutas de los sitios.
static string NormalizarSlug(string texto)
    => texto.Trim().ToLowerInvariant().Replace(' ', '-');
