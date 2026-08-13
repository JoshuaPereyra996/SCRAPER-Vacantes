# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Qué es

Aplicación de consola C# / .NET 8 que hace **una** búsqueda de empleo en **OCC.com.mx**,
**Computrabajo MX**, **Trabajos.mx** o **Reclutalia** (los tres primeros con Playwright /
Chromium headless; Reclutalia por API JSON pública sin navegador), extrae las vacantes de la
primera página de resultados y las guarda como JSON. Uso personal, de bajo volumen. El sitio
se elige con `--sitio occ|computrabajo|trabajos|reclutalia` (o en `appsettings.json` →
`Busqueda:sitio`).

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
dotnet run -- --sitio occ --empleo "contador" --ciudad "guadalajara"
dotnet run -- --sitio computrabajo --empleo "analista" --ciudad "ciudad-de-mexico"

# Ejecutar con diagnóstico (OCC: detalles interceptados; Computrabajo: vuelca el HTML
# de la primera página de detalle en output/_debug_detalle_ct.html para afinar selectores)
DEBUG_RESPONSES=1 dotnet run
```

No hay suite de pruebas. La forma de "probar" es ejecutar y revisar los archivos en
`output/` (relativo a `bin/Debug/net8.0/`, vía `AppContext.BaseDirectory`).

## Entorno: detalle importante de runtime

El SDK instalado puede ser **.NET 10**, no 8. El proyecto apunta a `net8.0` pero incluye
`<RollForward>LatestMajor</RollForward>` en el `.csproj` para ejecutarse sobre el runtime
mayor disponible. Sin eso, `dotnet run` falla con "Microsoft.NETCore.App 8.0.0 not found".
No cambies el target a net10.0 para "arreglar" esto; el RollForward es la solución.

## Arquitectura multi-sitio

Cada sitio implementa `ISitioScraper` (`Services/ISitioScraper.cs`):
`Task<ResultadoScrape?> BuscarAsync(empleo, ciudad)`, donde `ResultadoScrape` lleva el
contenido **crudo** (con su extensión: "json" u "html") y la `List<Vacante>` ya parseada.
Cada scraper hace su propia navegación, extracción Y parseo. `Program.cs` solo elige el
scraper en un `switch` según `--sitio` y guarda los dos archivos. `Models/Vacante` es común
(incluye `Fuente`). **Para añadir un sitio**: nuevo `XxxScraperService : ISitioScraper` +
`XxxConstants` + caso en el `switch`. No metas lógica específica de sitio en `Program.cs`.

Los dos sitios usan mecanismos de datos **completamente distintos**:

## OCC: datos vía JSON interceptado

**El supuesto inicial (del spec) de que `api-collector.occ.com.mx/offer/search` devuelve los
datos es FALSO**: ese endpoint es solo telemetría y responde `"OK"`.

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

## Estructura del JSON de detalle de OCC (para el parser)

`Services/OccVacanteParser.cs` mapea el array de detalles a `Models/Vacante`. Cada detalle es
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

Si OCC cambia estos nombres, ajusta los mapeos en `OccVacanteParser.Parsear` y las claves en
los helpers del mismo archivo.

## Computrabajo: datos vía HTML (SSR)

Mucho más simple que OCC. La página de resultados (`/trabajo-de-{empleo}-en-{ciudad}`) está
renderizada en el servidor: cada vacante es un `<article class="box_offer">` con título,
empresa, ubicación, salario y fecha **en el HTML**. `Services/ComputrabajoScraperService.cs`:

1. Navega a la página de resultados y extrae cada tarjeta con los selectores de
   `ComputrabajoConstants` (título, empresa, ubicación, salario, fecha, `data-id`=jobid).
2. La **descripción NO está en la lista**: se obtiene **navegando a la página de cada oferta**
   (selector `[div-link="oferta"]`). Esa página da **403 por curl/HTTP plano** — requiere el
   navegador real, por eso se hace con Playwright dentro de la sesión.
3. El crudo guardado es el HTML de la página de resultados (`raw_computrabajo_*.html`).

Notas: el salario sale del `<span>` padre del icono `.i_salary` (ver `SalarioLocator`); el
selector de descripción `p.mbB` solo NO sirve (hay varios; uno es el banner "Ocultaste esta
oferta") — por eso se ancla al contenedor `[div-link="oferta"]`.

## Reclutalia: datos vía API JSON pública (sin navegador)

El más simple de todos y el único que **no usa Playwright**. Reclutalia es un SPA de Next.js
que carga las vacantes desde una API REST pública sin autenticación. `Services/ReclutaliaScraperService.cs`:

1. Hace **un solo** GET HTTP con `HttpClient` a
   `https://api.reclutalia.com/job-offers/search?tags={empleo}&offset=0&limit=50&channel=WEB&referer=WEB&env=production`.
   Los parámetros `channel`/`referer`/`env` son **fijos y obligatorios** (sin un `channel`
   válido la API responde `400 "No existe el valor de channel ingresado"`).
2. La respuesta trae `data.jobOffer[]`; cada vacante ya incluye título, empresa, ubicación,
   salario, fecha y **descripción completa** (el endpoint de detalle `/job-offers/{code}`
   devuelve la misma descripción, así que **no** se visita cada vacante).
3. La búsqueda de la API es por palabra clave (`tags`); es de **una sola palabra** (frases
   multi-palabra suelen dar 0). La **ubicación NO se filtra por slug de ciudad** (el sitio
   filtra por geocoordenadas de Google Places), así que el filtro por ciudad se hace **del
   lado del cliente** comparando la ciudad (normalizada, sin acentos) contra la dirección de
   cada vacante. Si ninguna coincide con la ciudad, se devuelven todas las del término.
4. El crudo guardado es el JSON de la API (`raw_reclutalia_*.json`). La URL pública de cada
   vacante es `https://reclutalia.com/job-offers/?jobOffer={code}`.

Estructura del JSON (para el parser, en la sección `about` de cada oferta): `about.title`
(título), `company.tradeName` (empresa), `places.address.formattedAddress` (ubicación),
`about.salary.{minimum,maximum,salaryFixed,salaryRange}` (salario, se formatea a
"$25,000 - $28,000 Mensual"), `publicationDate`, `about.description` y `code` (para la URL).
Punto de fragilidad: si la API cambia estos nombres o los params fijos, ajusta
`ReclutaliaConstants` y `ReclutaliaScraperService.Parsear`.

## Convenciones del código

- **Todas** las URLs, hosts, patrones de ruta, selectores y regex de cada sitio viven en su
  archivo de constantes (`OccConstants.cs`, `ComputrabajoConstants.cs`). No incrustes literales.
- Comentarios y mensajes de consola en **español**.
- Salida JSON con `WriteIndented = true` y `JavaScriptEncoder.UnsafeRelaxedJsonEscaping`
  (para no escapar acentos). Definido en `Program.cs`.
- `Program.cs` devuelve códigos de salida distintos por tipo de fallo (1=args, 2=scraper,
  3=sin datos, 4=error guardando crudo, 5=error parseando).
- `appsettings.json` usa claves tipo `"// xxx"` como comentarios simulados (el lector de
  configuración de .NET no admite comentarios reales); el código las ignora.

## Punto de fragilidad principal

Ambos scrapers dependen de selectores/patrones del HTML de cada sitio:
- **OCC**: `OccConstants.SelectorTarjeta` y `OccConstants.RegexIdDetalle`. Si reporta
  `Vacantes encontradas: 0` o `Detalles obtenidos: 0`, OCC cambió uno de esos.
- **Computrabajo**: los selectores de `ComputrabajoConstants` (`SelectorTarjeta`,
  `SelectorDescripcionDetalle`, etc.). Si reporta `Vacantes encontradas: 0` o las
  descripciones salen vacías/equivocadas, cambió el HTML.

Depura con `headless: false` en appsettings.json + `DEBUG_RESPONSES=1` (Computrabajo vuelca
el HTML de la primera página de detalle para reinspeccionar el selector de descripción).
