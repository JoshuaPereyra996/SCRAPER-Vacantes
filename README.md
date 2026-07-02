# Scraper de vacantes (OCC.com.mx y Computrabajo)

Aplicación de consola en **C# / .NET 8** que realiza **una** búsqueda de empleo en
[OCC.com.mx](https://www.occ.com.mx) o en
[Computrabajo MX](https://mx.computrabajo.com), usando un navegador real (Playwright),
extrae las vacantes con su URL pública y guarda los resultados en archivos JSON.

El sitio se elige con el parámetro `--sitio occ|computrabajo` (o en `appsettings.json`).

> ⚠️ **Por qué Playwright y no una llamada HTTP directa:**
> ambos sitios requieren una sesión real del navegador (cookies y cabeceras que genera
> el JS / que protegen el contenido). Computrabajo además responde **403** a la página
> de detalle si la pides sin navegador. Por eso un Chromium real carga las páginas.

## Cómo funciona cada sitio

Cada sitio implementa la interfaz `ISitioScraper` (`Services/ISitioScraper.cs`) y se
encarga de su propia navegación, extracción y parseo a `Vacante`.

### OCC (`Services/OccScraperService.cs`) — datos vía JSON interceptado

1. La página de resultados (`/empleos/de-{empleo}/en-{ciudad}/`) incrusta la lista de
   ids pero **no el contenido**. El POST a `api-collector.occ.com.mx/offer/search` es
   solo **telemetría** (responde `"OK"`), no datos.
2. El contenido real lo da `https://oferta.occ.com.mx/offer/{id}/d/j` (JSON), que la
   página dispara al **seleccionar una tarjeta** y requiere la sesión del navegador.
3. El scraper hace **clic en cada tarjeta** e **intercepta** esas respuestas JSON.
4. Resultado crudo: un array JSON (`raw_occ_*.json`). ~21 vacantes por búsqueda.

### Computrabajo (`Services/ComputrabajoScraperService.cs`) — datos vía HTML (SSR)

1. La página de resultados (`/trabajo-de-{empleo}-en-{ciudad}`) está renderizada en el
   servidor: cada vacante es un `<article class="box_offer">` con título, empresa,
   ubicación, salario y fecha **directamente en el HTML**.
2. El scraper extrae esos campos de cada tarjeta con selectores de Playwright.
3. La **descripción completa** no está en la lista: se obtiene **visitando la página de
   cada oferta** (`div[div-link="oferta"]`), que da 403 sin navegador real.
4. Resultado crudo: el HTML de la página de resultados (`raw_computrabajo_*.html`).
   ~20 vacantes por búsqueda.

---

## Requisitos previos

| Requisito | Notas |
|-----------|-------|
| **SDK de .NET capaz de compilar `net8.0`** | El SDK **8.0.x** o cualquier SDK más reciente (p. ej. **10.0.x**) sirve. Verifica con `dotnet --list-sdks`. |
| **Navegador Chromium de Playwright** | Se descarga una sola vez (ver más abajo). |
| **PowerShell (`pwsh`)** *(opcional)* | Solo para el método con `playwright.ps1`. Si no lo tienes, usa la herramienta global (ver abajo). |

> ✅ **Verificado en este entorno:** el proyecto compila con **.NET SDK 10.0.x**
> apuntando a `net8.0` (no es necesario instalar el SDK 8 por separado).

### Instalar el SDK de .NET (solo si no tienes ninguno compatible)

```bash
# Opción A: Homebrew (instala el SDK más reciente)
brew install dotnet-sdk

# Opción B: script oficial de Microsoft (canal 8.0)
curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 8.0
```

Verifica:

```bash
dotnet --list-sdks   # debe listar un 8.0.x o superior
```

---

## Instalación del proyecto

Desde la carpeta raíz del proyecto (`SCRAPER Vacantes`):

```bash
# 1. Restaurar paquetes NuGet (descarga Microsoft.Playwright)
dotnet restore

# 2. Compilar
dotnet build

# 3. Descargar el navegador Chromium que usará Playwright (UNA sola vez)
#    -- Método recomendado SIN pwsh (verificado en este entorno): --
dotnet tool install --global Microsoft.Playwright.CLI
playwright install chromium
```

> Tras `dotnet tool install`, si una terminal nueva no encuentra el comando `playwright`,
> añade la carpeta de herramientas al PATH:
> ```bash
> export PATH="$PATH:$HOME/.dotnet/tools"
> ```
> (para hacerlo permanente, agrégalo a tu `~/.zprofile`).

### Alternativa con PowerShell (`pwsh`)

Si prefieres usar el script que genera Playwright:

```bash
brew install --cask powershell   # si no tienes pwsh
pwsh bin/Debug/net8.0/playwright.ps1 install chromium
```

Al instalar Chromium se descargan tres componentes en
`~/Library/Caches/ms-playwright/`: `chromium-<build>` (navegador completo, para
`headless: false`), `chromium_headless_shell-<build>` (ligero, para `headless: true`)
y `ffmpeg-<build>` (grabación de video, opcional).

---

## Configuración

Edita `appsettings.json` para definir la búsqueda y el comportamiento del navegador:

```jsonc
{
  "Busqueda": {
    "sitio": "occ",                // 'occ' o 'computrabajo'
    "empleo": "analista",          // palabra clave del puesto
    "ciudad": "ciudad-de-mexico"   // ciudad en formato slug
  },
  "Playwright": {
    "headless": true,              // false = ver el navegador (depuración)
    "timeoutMs": 60000,            // espera máxima para capturar el endpoint
    "delayMs": 2000,               // espera extra para no parecer bot
    "userAgent": "Mozilla/5.0 ..." // User-Agent realista
  },
  "Paginacion": {
    "habilitada": false,           // por defecto solo la primera página
    "paginaInicial": 1,
    "maxPaginas": 1
  }
}
```

> Las claves que empiezan con `// ` dentro del JSON son **comentarios simulados**
> (el lector de configuración de .NET no admite comentarios reales). El código las ignora.

---

## Interfaz gráfica (GUI)

La carpeta `gui/` contiene una app de escritorio en **Python/Tkinter** que usa el
scraper .NET como motor (subproceso), acumula varias búsquedas en una sesión y
exporta todo a un único **Word (.docx)** con los JSON concatenados.

```bash
# Dependencia (solo para exportar a Word)
pip install python-docx

# Lanzar la GUI (requiere dotnet + Chromium de Playwright ya instalados, ver arriba)
python3 gui/app.py
```

Flujo: elige la bolsa (OCC / Computrabajo; Indeed próximamente), escribe puesto y
ciudad y pulsa **Iniciar Búsqueda**. Cada resultado se anexa al caché de sesión
(`gui/session_cache.json`), que sobrevive si la app se cierra. **Siguiente Búsqueda**
limpia los campos conservando lo acumulado. **Finalizar y Exportar** genera el .docx:
un array JSON válido con todas las vacantes deduplicadas por `fuente`+`jobid` (cada
una con el campo extra `busquedas` indicando qué búsquedas la produjeron), listo para
un lector de coincidencias.

Módulos: `app.py` (ventana), `scraper_runner.py` (subproceso `dotnet run` + lectura
del JSON de salida), `session_store.py` (caché en disco), `docx_export.py` (Word).

---

## Uso

### Búsqueda con los valores de `appsettings.json`

```bash
dotnet run
```

### Búsqueda con parámetros por línea de comandos (sobrescriben el JSON)

```bash
# OCC (por defecto)
dotnet run -- --empleo "contador" --ciudad "monterrey"

# Computrabajo
dotnet run -- --sitio computrabajo --empleo "contador" --ciudad "guadalajara"
```

> Cada ejecución realiza **una sola búsqueda** y captura la **primera página** de
> resultados.

---

## Resultados

Tras una ejecución exitosa se generan dos archivos en la carpeta `output/`:

| Archivo | Contenido |
|---------|-----------|
| `raw_{sitio}_{empleo}_{ciudad}_{timestamp}.{json\|html}` | El contenido **crudo** tal cual lo entregó el sitio (JSON en OCC, HTML en Computrabajo). |
| `vacantes_{sitio}_{empleo}_{ciudad}_{timestamp}.json` | Array **limpio** de objetos `Vacante`. |

Cada `Vacante` incluye al menos:

- Fuente (`occ` / `computrabajo`)
- Título del puesto
- Empresa
- Ubicación / ciudad
- Salario (si existe)
- Fecha de publicación
- Descripción / resumen (si viene)
- ID de la vacante (`jobid`)
- **URL pública navegable**, p. ej.:
  `https://www.occ.com.mx/empleos/de-analista/en-ciudad-de-mexico/?jobid=12345678`

El JSON se serializa con indentado legible y **respetando los acentos** (sin escapar
caracteres no-ASCII).

---

## Estructura del proyecto

```
SCRAPER Vacantes/
├── OccScraper.csproj                  # Proyecto de consola .NET 8 + Playwright
├── appsettings.json                   # Parámetros configurables (incluye 'sitio')
├── README.md                          # Este archivo
├── Program.cs                         # Orquestación + selección de sitio
├── Models/
│   └── Vacante.cs                     # Modelo de salida (común a todos los sitios)
├── Services/
│   ├── ISitioScraper.cs               # Interfaz común + OpcionesScraper + ResultadoScrape
│   ├── OccConstants.cs                # OCC: endpoints, URLs, selectores
│   ├── OccScraperService.cs           # OCC: Playwright + interceptación JSON
│   ├── OccVacanteParser.cs            # OCC: JSON crudo → Vacante
│   ├── ComputrabajoConstants.cs       # Computrabajo: URLs y selectores
│   └── ComputrabajoScraperService.cs  # Computrabajo: extracción de HTML SSR
└── output/                            # Resultados (se crea en tiempo de ejecución)
```

> Para añadir otro sitio: crea `XxxScraperService : ISitioScraper`, sus constantes, y
> registra el caso en el `switch` de `Program.cs`.

---

## Solución de problemas

| Problema | Causa probable / solución |
|----------|---------------------------|
| `The framework 'Microsoft.NETCore.App', version '8.0.0' was not found` | No está el runtime 8.0. El proyecto ya incluye `<RollForward>LatestMajor</RollForward>` para ejecutarse sobre un runtime mayor (ej. 10.0); si aun así falla, instala el runtime/SDK 8.0. |
| `Executable doesn't exist at .../chromium...` | Falta descargar el navegador: `playwright install chromium`. |
| `pwsh: command not found` | Usa la herramienta global `Microsoft.Playwright.CLI` (ver instalación). |
| `Vacantes encontradas: 0` | OCC cambió el HTML (cambió el selector de tarjetas). Pon `headless: false` para observar y revisa `OccConstants.SelectorTarjeta`. |
| Se obtienen menos detalles que tarjetas (ej. 21/22) | Normal: alguna tarjeta patrocinada/anuncio no tiene detalle JSON. |
| Activar diagnóstico | Ejecuta con la variable `DEBUG_RESPONSES=1` para registrar cada detalle capturado. |
| Caracteres con acentos mal mostrados | Asegúrate de abrir el JSON con codificación UTF-8. |

---

## Notas legales y de uso responsable

- Proyecto para **uso personal y de bajo volumen**.
- Respeta los **Términos de Servicio** de OCC.
- No martillees el servidor: **una búsqueda por ejecución**, con esperas entre acciones.
