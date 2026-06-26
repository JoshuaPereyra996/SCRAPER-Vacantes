# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Qué es

Aplicación de consola C# / .NET 8 que hace **una** búsqueda de empleo en OCC.com.mx
usando Playwright (Chromium headless), extrae las vacantes de la primera página de
resultados y las guarda como JSON. Uso personal, de bajo volumen.

## Comandos

```bash
# Restaurar, compilar
dotnet restore
dotnet build

# Descargar el navegador de Playwright (UNA sola vez; este entorno no tiene pwsh)
dotnet tool install --global Microsoft.Playwright.CLI
export PATH="$PATH:$HOME/.dotnet/tools"
playwright install chromium

# Ejecutar (usa appsettings.json)
dotnet run

# Ejecutar con parámetros (sobrescriben appsettings.json)
dotnet run -- --empleo "contador" --ciudad "guadalajara"

# Ejecutar con diagnóstico (registra cada respuesta de detalle interceptada)
DEBUG_RESPONSES=1 dotnet run
```

No hay suite de pruebas. La forma de "probar" es ejecutar y revisar los archivos en
`output/` (relativo a `bin/Debug/net8.0/`, vía `AppContext.BaseDirectory`).

## Entorno: detalle importante de runtime

El SDK instalado puede ser **.NET 10**, no 8. El proyecto apunta a `net8.0` pero incluye
`<RollForward>LatestMajor</RollForward>` en el `.csproj` para ejecutarse sobre el runtime
mayor disponible. Sin eso, `dotnet run` falla con "Microsoft.NETCore.App 8.0.0 not found".
No cambies el target a net10.0 para "arreglar" esto; el RollForward es la solución.

## Arquitectura: cómo se obtienen los datos realmente

Esto es lo más importante y no es obvio leyendo el código aislado. **El supuesto inicial
(documentado en el spec) de que `api-collector.occ.com.mx/offer/search` devuelve los datos
es FALSO**: ese endpoint es solo telemetría y responde `"OK"`.

El flujo real, implementado en `Services/OccScraperService.cs`:

1. Navega a `https://www.occ.com.mx/empleos/de-{empleo}/en-{ciudad}/` con
   `WaitUntil = DOMContentLoaded` (NetworkIdle **nunca** se cumple por la analítica
   constante del sitio — no lo uses).
2. Registra un handler `page.Response` que **intercepta** las respuestas cuya URL coincide
   con `OccConstants.RegexIdDetalle` (`/offer/(\d+)/d/j`). Ese es el endpoint de datos real:
   `https://oferta.occ.com.mx/offer/{id}/d/j`, que devuelve el JSON rico de una vacante.
3. Hace **clic en cada tarjeta** (`OccConstants.SelectorTarjeta` =
   `[data-offers-grid-offer-item-container]`) para que la propia página dispare el fetch del
   detalle con la sesión/cookies correctas. Llamar ese endpoint por fuera (curl, APIRequest
   de Playwright, fetch manual) da **404 / NotFoundOfferDetail** — debe dispararlo la página.
4. Acumula los detalles en un diccionario (dedupe por id) y los combina en un array JSON.

Es normal obtener ~21 de 22 detalles: alguna tarjeta es patrocinada/anuncio sin detalle JSON.

## Estructura del JSON de detalle (para el parser)

`Services/VacanteParser.cs` mapea el array de detalles a `Models/Vacante`. Cada detalle es
`{ "o": {...}, "c": {...}, ... }`; los campos relevantes están en la sección **`o`** con
nombres abreviados:

| Campo `o.` | Significado |
|-----------|-------------|
| `eoi` | id de la vacante (jobid) |
| `t` / `ltr` / `lc` | título |
| `cn` | empresa (también `c.cn`) |
| `c` + `l` | municipio + ciudad/estado (se combinan en `ubicacion`) |
| `lss` | salario ya formateado, ej. "$13,000 - $18,000 Mensual" |
| `dluf` / `dlur` | fecha texto / fecha relativa |
| `ld` | descripción en HTML (el parser quita tags y decodifica entidades) |
| `ur` | URL relativa canónica → se antepone `OccConstants.SitioBase` para la URL pública |

Si OCC cambia estos nombres, ajusta los mapeos en `VacanteParser.Parsear` y las claves en
los helpers del mismo archivo.

## Convenciones del código

- **Todas** las URLs, hosts, patrones de ruta, selectores y regex viven en
  `Services/OccConstants.cs`. No incrustes literales de OCC en otros archivos.
- Comentarios y mensajes de consola en **español**.
- Salida JSON con `WriteIndented = true` y `JavaScriptEncoder.UnsafeRelaxedJsonEscaping`
  (para no escapar acentos). Definido en `Program.cs`.
- `Program.cs` devuelve códigos de salida distintos por tipo de fallo (1=args, 2=scraper,
  3=sin datos, 4=error guardando crudo, 5=error parseando).
- `appsettings.json` usa claves tipo `"// xxx"` como comentarios simulados (el lector de
  configuración de .NET no admite comentarios reales); el código las ignora.

## Punto de fragilidad principal

El scraper depende de dos cosas del HTML de OCC: el selector de tarjetas
(`SelectorTarjeta`) y la forma de la URL de detalle (`RegexIdDetalle`). Si una corrida
reporta `Vacantes encontradas: 0` o `Detalles obtenidos: 0`, casi siempre es porque OCC
cambió uno de esos dos. Depura con `headless: false` en appsettings.json + `DEBUG_RESPONSES=1`.
