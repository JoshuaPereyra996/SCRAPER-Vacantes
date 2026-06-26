using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using OccScraper.Services;

// ============================================================================
//  OccScraper — punto de entrada
//  Flujo: leer config -> lanzar Playwright -> interceptar /offer/search
//         -> guardar JSON crudo -> parsear -> guardar JSON limpio.
// ============================================================================

// --- 1. Leer configuración (appsettings.json + args de consola) --------------
// Los args sobrescriben el JSON. Uso: dotnet run -- --empleo "contador" --ciudad "monterrey"
var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
    .AddCommandLine(args)
    .Build();

var empleo = config["empleo"] ?? config["Busqueda:empleo"];
var ciudad = config["ciudad"] ?? config["Busqueda:ciudad"];

if (string.IsNullOrWhiteSpace(empleo) || string.IsNullOrWhiteSpace(ciudad))
{
    Console.Error.WriteLine("Error: faltan parámetros 'empleo' y/o 'ciudad'.");
    Console.Error.WriteLine("Configúralos en appsettings.json o pásalos por consola:");
    Console.Error.WriteLine("  dotnet run -- --empleo \"analista\" --ciudad \"ciudad-de-mexico\"");
    return 1;
}

// Normalizar a slug simple (minúsculas, sin espacios extremos, espacios -> guiones).
empleo = NormalizarSlug(empleo);
ciudad = NormalizarSlug(ciudad);

// Opciones de Playwright desde la sección "Playwright".
var opciones = new OccScraperOptions(
    Headless: config.GetValue("Playwright:headless", true),
    TimeoutMs: config.GetValue("Playwright:timeoutMs", 60000),
    DelayMs: config.GetValue("Playwright:delayMs", 2000),
    UserAgent: config["Playwright:userAgent"]
        ?? "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");

// --- Paginación (PREPARADA pero desactivada por defecto) ---------------------
// Por defecto solo se captura la primera página. Para habilitar la paginación:
//   1) Poner "Paginacion:habilitada" = true en appsettings.json.
//   2) Extender OccScraperService para iterar el parámetro 'pn' (OccConstants.ParametroPagina)
//      hasta "Paginacion:maxPaginas", acumulando las respuestas.
// var paginacionHabilitada = config.GetValue("Paginacion:habilitada", false);
// var maxPaginas = config.GetValue("Paginacion:maxPaginas", 1);

Console.WriteLine($"Búsqueda: empleo='{empleo}', ciudad='{ciudad}' (headless={opciones.Headless})");

// --- 2. Ejecutar el scraper --------------------------------------------------
string? jsonCrudo;
try
{
    var scraper = new OccScraperService(opciones);
    jsonCrudo = await scraper.BuscarAsync(empleo, ciudad);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Error inesperado ejecutando el scraper: {ex.Message}");
    return 2;
}

if (string.IsNullOrWhiteSpace(jsonCrudo))
{
    Console.Error.WriteLine("No se capturó ninguna respuesta de /offer/search. Nada que guardar.");
    Console.Error.WriteLine("Sugerencia: prueba con headless=false y/o aumenta timeoutMs en appsettings.json.");
    return 3;
}

// --- 3. Preparar carpeta y nombres de salida ---------------------------------
var carpetaSalida = Path.Combine(AppContext.BaseDirectory, "output");
Directory.CreateDirectory(carpetaSalida);

var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
var rutaCruda = Path.Combine(carpetaSalida, $"raw_{empleo}_{ciudad}_{timestamp}.json");
var rutaLimpia = Path.Combine(carpetaSalida, $"vacantes_{empleo}_{ciudad}_{timestamp}.json");

// Opciones de serialización: indentado legible y acentos sin escapar.
var jsonOpts = new JsonSerializerOptions
{
    WriteIndented = true,
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
};

// --- 4. Guardar el JSON crudo TAL CUAL (sin modificar) -----------------------
try
{
    await File.WriteAllTextAsync(rutaCruda, jsonCrudo);
    Console.WriteLine($"JSON crudo guardado en: {rutaCruda}");
}
catch (Exception ex)
{
    Console.Error.WriteLine($"No se pudo guardar el JSON crudo: {ex.Message}");
    return 4;
}

// --- 5. Parsear a modelo limpio y guardar ------------------------------------
try
{
    var vacantes = VacanteParser.Parsear(jsonCrudo, empleo, ciudad);
    var jsonLimpio = JsonSerializer.Serialize(vacantes, jsonOpts);
    await File.WriteAllTextAsync(rutaLimpia, jsonLimpio);
    Console.WriteLine($"JSON limpio guardado en: {rutaLimpia} ({vacantes.Count} vacantes)");
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

// Convierte un texto a un slug simple compatible con las rutas de OCC.
static string NormalizarSlug(string texto)
    => texto.Trim().ToLowerInvariant().Replace(' ', '-');
