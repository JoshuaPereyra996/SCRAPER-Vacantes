# OccScraper — Scraper de vacantes de OCC.com.mx

Aplicación de consola en **C# / .NET 8** que realiza **una** búsqueda de empleo en
[OCC.com.mx](https://www.occ.com.mx), intercepta la respuesta del endpoint interno
`/offer/search` mediante un navegador real (Playwright), extrae las vacantes con su
URL pública y guarda los resultados en archivos JSON.

> ⚠️ **Por qué Playwright y no una llamada HTTP directa:**
> los endpoints internos de OCC requieren la sesión real del navegador (cookies y
> cabeceras que genera el JavaScript del sitio). No se pueden llamar a mano. Por eso
> dejamos que un Chromium real cargue la página y **interceptamos** las respuestas JSON.

## Cómo funciona realmente (arquitectura descubierta)

Tras analizar el tráfico real de OCC, el flujo de datos es este:

1. La página de resultados (`/empleos/de-{empleo}/en-{ciudad}/`) se renderiza en el
   servidor e **incrusta la lista de ids** de las vacantes, pero **no su contenido**.
2. El POST a `api-collector.occ.com.mx/offer/search` resultó ser solo **telemetría**
   (responde `"OK"`), **no** la fuente de datos como se pensó al inicio.
3. El **contenido completo** de cada vacante lo entrega el endpoint
   `https://oferta.occ.com.mx/offer/{id}/d/j` (JSON), que la página dispara al
   **seleccionar una tarjeta** y que requiere la sesión del navegador.

Por eso el scraper:

1. Carga la página de resultados con Chromium.
2. Registra un handler que **intercepta** las respuestas de `/offer/{id}/d/j`.
3. Hace **clic en cada tarjeta** (`data-offers-grid-offer-item-container`) para que el
   sitio pida su detalle con la sesión correcta, y captura ese JSON rico.
4. Combina todos los detalles, los guarda crudos y los parsea a `Vacante`.

> Resultado típico: ~21–22 vacantes por búsqueda (primera página), cada una con título,
> empresa, ubicación, salario, fecha, descripción y URL pública navegable.

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
    "empleo": "analista",          // palabra clave del puesto
    "ciudad": "ciudad-de-mexico"   // ciudad en formato slug de OCC
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

## Uso

### Búsqueda con los valores de `appsettings.json`

```bash
dotnet run
```

### Búsqueda con parámetros por línea de comandos (sobrescriben el JSON)

```bash
dotnet run -- --empleo "contador" --ciudad "monterrey"
```

> Cada ejecución realiza **una sola búsqueda** y captura la **primera página** de
> resultados. La paginación queda preparada pero desactivada por defecto.

---

## Resultados

Tras una ejecución exitosa se generan dos archivos en la carpeta `output/`:

| Archivo | Contenido |
|---------|-----------|
| `raw_{empleo}_{ciudad}_{timestamp}.json` | El JSON **crudo y completo** tal cual lo devolvió `/offer/search`. |
| `vacantes_{empleo}_{ciudad}_{timestamp}.json` | Array **limpio** de objetos `Vacante`. |

Cada `Vacante` incluye al menos:

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
├── OccScraper.csproj            # Proyecto de consola .NET 8 + Playwright
├── appsettings.json             # Parámetros configurables
├── README.md                    # Este archivo
├── Program.cs                   # Orquestación
├── Models/
│   └── Vacante.cs               # Modelo de salida
├── Services/
│   ├── OccConstants.cs          # Endpoints, URLs base y patrones
│   ├── OccScraperService.cs     # Playwright + interceptación
│   └── VacanteParser.cs         # JSON crudo → modelo limpio
└── output/                      # Resultados (se crea en tiempo de ejecución)
```

> **Estado actual:** proyecto completo y **compilando sin errores**. Todos los
> archivos están entregados.

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
