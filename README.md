# VlmHub

VlmHub es una aplicación de consola en **.NET 8** para seleccionar documentos almacenados en Microsoft SQL Server, descargarlos de forma progresiva, procesarlos mediante uno o más **VLM Servers** y persistir sus resultados de forma recuperable y eficiente.

La aplicación está diseñada para trabajar con selecciones grandes sin cargar el lote completo en memoria. Cada ejecución conserva su selección, estado, resultados e historial local, permitiendo continuar el trabajo después de una caída sin repetir resultados ya persistidos.

## Arquitectura

La solución mantiene tres proyectos principales y un único ejecutable:

```text
VlmHub.sln
│
├── VlmHub.Console
│   └── Orquestación, interfaz, descarga, worker y recuperación
│
├── VlmHub.Sql
│   └── Microsoft SQL Server + historial SQLite local
│
└── VlmHub.Balancer
    └── Distribución y procesamiento mediante VLM Servers
```

### Responsabilidades

**VlmHub.Console**

- Login y navegación por SQL Server.
- Selección de base de datos, tabla y columna documental.
- Selección de filas por PK simple o compuesta.
- Creación del manifiesto permanente `selection.json`.
- Descarga progresiva de documentos.
- Lanzamiento del procesamiento en un worker independiente.
- Recuperación de ejecuciones interrumpidas.
- Seguimiento del log en tiempo real.

**VlmHub.Sql**

- Acceso a Microsoft SQL Server.
- Consulta de bases, tablas, columnas y PK.
- Escritura progresiva de la selección mediante `SqlDataReader`.
- Creación y migración de la tabla auxiliar.
- Persistencia idempotente de resultados.
- Historial local SQLite mediante `Document` y `ProcessingUnit`.

**VlmHub.Balancer**

- Monitoreo de VLM Servers.
- Distribución de unidades entre servidores disponibles.
- Prioridad y cambio de modelos.
- Procesamiento de imágenes.
- Conversión de PDF a páginas.
- Reintentos y recuperación ante errores de infraestructura.
- Escritura incremental de resultados.
- Control del número de documentos residentes en memoria.

## Flujo general

```text
Usuario selecciona filas
        │
        ▼
control/selection.json
        │
        ▼
worker independiente
        │
        ▼
leer ventana pequeña
        │
        ▼
descargar documentos
        │
        ▼
VlmHub.Balancer
        │
        ├── VLM Server 1
        ├── VLM Server 2
        └── VLM Server N
        │
        ▼
results/<ItemId>.json
        │
        ├── SQLite
        │
        └── SQL Server
        │
        ▼
eliminar temporal
        │
        ▼
siguiente ventana
```

## Requisitos

- **.NET 8 SDK** para compilar.
- Microsoft SQL Server accesible desde el equipo que ejecuta VlmHub.
- Usuario SQL con permisos sobre las bases/tablas que se desean consultar y permiso para crear/modificar la tabla auxiliar correspondiente.
- Uno o más VLM Servers accesibles por red.
- Acceso HTTP/HTTPS a los documentos configurados en la columna documental.

Dependencias principales:

- `Microsoft.Data.SqlClient`
- `Microsoft.Data.Sqlite`
- `Spectre.Console`
- `PDFtoImage`

En Linux se recomienda disponer de `setsid` para desacoplar completamente el worker de la terminal interactiva. En macOS se utiliza `nohup` cuando está disponible. En Windows el worker se inicia sin una consola adicional visible.

## Configuración

### `vlmhub.json`

Controla los límites operacionales del procesamiento.

```json
{
  "servers_file": "servidores.json",
  "model_priority": [
    "PaddleOCR-VL-1.6-f16",
    "Qwen3VL-2B-Instruct-F16",
    "Deepseek-OCR-2-q8"
  ],
  "max_attempts": 3,
  "max_documents_in_memory": 32,
  "max_parallel_document_preparation": 2,
  "max_parallel_downloads": 4,
  "download_max_attempts": 3,
  "download_timeout_seconds": 90,
  "sql_upload_max_attempts": 3,
  "sql_upload_retry_seconds": 3,
  "sql_upload_cooldown_seconds": 30,
  "keep_temporary_files_on_failure": false
}
```

### Parámetros principales

| Parámetro | Función |
|---|---|
| `servers_file` | Archivo con la definición de VLM Servers. |
| `model_priority` | Orden de prioridad de los modelos. |
| `max_attempts` | Máximo de intentos VLM por unidad. |
| `max_documents_in_memory` | Tamaño máximo de la ventana documental. |
| `max_parallel_document_preparation` | Preparaciones de documentos simultáneas. |
| `max_parallel_downloads` | Descargas simultáneas. |
| `download_max_attempts` | Reintentos de descarga. |
| `download_timeout_seconds` | Timeout individual de descarga. |
| `sql_upload_max_attempts` | Intentos inmediatos de persistencia SQL. |
| `sql_upload_retry_seconds` | Espera entre reintentos SQL. |
| `sql_upload_cooldown_seconds` | Pausa temporal cuando SQL Server está indisponible. |
| `keep_temporary_files_on_failure` | Conserva o elimina temporales fallidos del balanceador. |

### `servidores.json`

Ejemplo:

```json
[
  {
    "Name": "VLM-Server_01",
    "URL": "192.168.1.20",
    "port": "5080",
    "Description": "Servidor VLM principal"
  },
  {
    "Name": "VLM-Server_02",
    "URL": "192.168.1.21",
    "port": "5080",
    "Description": "Servidor VLM secundario"
  }
]
```

El balanceador utiliza este archivo para conocer los servidores disponibles y su configuración.

## Compilación

Desde la raíz del proyecto:

```bash
dotnet restore VlmHub.sln
dotnet build VlmHub.sln -c Release
```

Para ejecutar directamente durante desarrollo:

```bash
dotnet run --project VlmHub.Console/VlmHub.Console.csproj
```

También puede publicarse como ejecutable:

```bash
dotnet publish VlmHub.Console/VlmHub.Console.csproj -c Release -o publish
```

## Uso

Al iniciar VlmHub aparece:

```text
=== VlmHub ===

1. Nuevo procesamiento
2. Recuperar procesamiento
0. Salir
```

### Nuevo procesamiento

El flujo interactivo es:

```text
Login SQL Server
    ↓
Base de datos
    ↓
Tabla
    ↓
Columna documental
    ↓
Selección de filas por PK
    ↓
Crear selection.json
    ↓
Lanzar worker
    ↓
Mostrar log en vivo
```

Después del login se puede retroceder entre las etapas de selección mediante:

```text
/volver
```

### Selección por PK

PK simple:

```text
1-20; 22; 30-50
```

PK compuesta:

```text
(2025,1); (2025,5)
```

Rango de PK compuesta:

```text
(2025,1)-(2025,20)
```

En un rango compuesto todos los componentes deben permanecer iguales salvo el último.

La aplicación envía los valores a SQL Server y evita realizar conversiones/validaciones duplicadas que el propio motor SQL puede resolver.

## Documentos

La referencia almacenada en SQL Server se normaliza utilizando como base:

```text
https://docudigital.ssffaa.cl/
```

También se reemplazan `\\` por `/` antes de construir la URL final.

Las descargas utilizan streaming directo a disco. Un fallo de descarga no consume intentos VLM.

## Procesamiento en segundo plano

Después de crear la selección, VlmHub inicia el mismo ejecutable en modo interno:

```text
--worker <directorio-de-procesamiento>
```

Este modo no debe invocarse manualmente durante el uso normal.

La terminal interactiva queda dedicada a mostrar en tiempo real las líneas del log correspondientes al `ProcessingId` actual.

### Cerrar la terminal

Cerrar la terminal interactiva no se utiliza como señal de vida del procesamiento. El worker queda desacoplado y puede continuar trabajando.

Al volver a iniciar VlmHub, la opción **Recuperar procesamiento** permite volver a adjuntarse a una ejecución incompleta. Si el worker sigue activo, VlmHub sigue su log en lugar de iniciar un segundo worker.

### Ctrl+C

`Ctrl+C` solicita explícitamente la cancelación del procesamiento completo.

La consola crea:

```text
control/cancel.requested
```

El worker detecta la señal y cancela cooperativamente. Si no termina durante el período de gracia, la consola intenta terminar el proceso worker.

## Estructura de una ejecución

Cada selección crea una carpeta independiente:

```text
processing/
└── <fecha>_<ProcessingId>/
    │
    ├── control/
    │   ├── run.json
    │   ├── selection.json
    │   ├── worker.lock
    │   ├── worker.pid
    │   └── items/
    │       ├── <ItemId>.json
    │       └── ...
    │
    ├── results/
    │   ├── <ItemId>.json
    │   ├── <ItemId>.json.ready
    │   └── ...
    │
    └── temp/
        └── documentos temporales
```

### Identificadores

**ProcessingId** identifica una selección completa.

**ItemId** identifica una ejecución documental concreta y se reutiliza como `ProcessingKey` remoto.

Esto permite reprocesar la misma PK en ejecuciones posteriores sin colisionar con resultados anteriores.

## Estados de un documento

```text
Pending
   ↓
Processing
   ↓
PendingUpload
   ↓
Completed
```

| Estado | Significado |
|---|---|
| `Pending` | Todavía no se ha terminado el procesamiento. |
| `Processing` | El documento está descargándose/preparándose/procesándose. |
| `PendingUpload` | El resultado local terminó, pero falta persistirlo correctamente en SQL Server. |
| `Completed` | La ejecución quedó registrada de forma permanente en SQL Server. |

`Completed` no implica necesariamente OCR/VLM exitoso. El resultado funcional se almacena aparte en `Success`.

## Recuperación ante caídas

Los archivos dentro de `control/` son la fuente de verdad para recuperar una ejecución.

Reglas principales:

```text
Completed
    → no reprocesar

PendingUpload
    → no ejecutar VLM
    → reintentar SQL Server

Processing + resultado .ready
    → PendingUpload
    → no repetir inferencia

Processing sin resultado final
    → Pending
    → reprocesar
```

El marcador:

```text
results/<ItemId>.json.ready
```

protege la ventana de caída entre la finalización del resultado local y la actualización del control documental.

La persistencia remota es idempotente mediante `ProcessingKey`, por lo que repetir una escritura después de perder la confirmación de SQL Server no genera una segunda fila.

## Resultado local

Imagen:

```json
{
  "tipo": "imagen",
  "contenido": "..."
}
```

PDF:

```json
{
  "tipo": "pdf",
  "contenido": {
    "pag_001": "...",
    "pag_002": "..."
  }
}
```

Los resultados finales quedan en:

```text
processing/<ProcessingId>/results/<ItemId>.json
```

## Tabla auxiliar SQL Server

Para cada tabla de origen se utiliza:

```text
<TablaOriginal>_VlmHubProcessing
```

Ejemplo:

```text
dbo.Documentos
    ↓
dbo.Documentos_VlmHubProcessing
```

La tabla contiene conceptualmente:

| Campo | Función |
|---|---|
| `Id` | PK interna. |
| `Original_<PK>` | Una columna por componente de la PK original. |
| `ProcessingKey` | `ItemId` único de la ejecución documental. |
| `Transcription` | JSON final generado. |
| `Success` | Resultado funcional del documento. |
| `NumPages` | Número de páginas/unidades documentales. |
| `ProcessedAt` | Fecha de finalización. |
| `ProcessingTime` | Tiempo de procesamiento VLM documental en segundos. |
| `Error` | Error general, cuando corresponde. |

La tabla se crea/migra de forma idempotente. Existe unicidad sobre `ProcessingKey` y un índice sobre la PK original junto a `Success` para consultar eficientemente si una fila ya tuvo al menos un procesamiento exitoso.

### `ProcessingTime`

`ProcessingTime` se mide con un reloj monotónico (`Stopwatch`).

Comienza cuando la **primera unidad/página realiza su primer envío real al VLM** y termina cuando la **última unidad del documento queda resuelta**.

Incluye:

- procesamiento VLM;
- reintentos;
- esperas posteriores al inicio de la primera unidad;
- distribución/cola entre unidades mientras el documento está en curso.

No incluye:

- descarga del documento;
- rasterización inicial del PDF;
- preparación previa al primer envío;
- persistencia final en SQL Server.

El valor se redondea a 3 decimales y se reutiliza tanto en SQL Server como en SQLite `Document.ProcessingTime`.

## SQLite local

El historial detallado se guarda en:

```text
data/vlmhub.db
```

Se utilizan dos tablas.

### `Document`

Registra una ejecución documental completa e incluye:

- `Id` (`ItemId`)
- `ProcessingId`
- `ServerHost`
- `DatabaseName`
- `SourceTable`
- `OriginalKey`
- `DocumentName`
- `ServerProcessingId`
- `Success`
- `ProcessingTime`
- `StartedAt`
- `FinishedAt`

`DocumentName` almacena el nombre del documento obtenido desde su referencia/URL.

### `ProcessingUnit`

Registra cada intento de una imagen o página:

- `DocumentId`
- `UnitIndex`
- `AttemptNumber`
- `Model`
- `Server`
- `Success`
- `FinishReason`
- `ProcessingTime`
- `ProcessedAt`
- `Transcription`
- `Error`

SQLite utiliza WAL y escritura incremental; no se acumula el historial completo en RAM.

## Logs

Todos los módulos escriben mediante el mismo sistema de logging:

```text
logs/
├── vlmhub_YYYYMMDD.log
└── ...
```

Los logs rotan por fecha y tamaño.

Cuando corresponde, cada línea puede incluir:

- `ProcessingId`
- `ItemId`
- PK
- módulo
- evento
- VLM Server
- modelo
- error

No se almacenan contraseñas ni transcripciones completas en el log operacional.

Para observar manualmente el log en Linux:

```bash
tail -n 100 -f "$(ls -t logs/vlmhub_*.log | head -1)"
```

## Manejo de memoria y recursos

VlmHub está diseñado para que el consumo de memoria no crezca proporcionalmente a la selección.

- La selección SQL utiliza `SqlDataReader` y `SequentialAccess`.
- `selection.json` se escribe mediante streaming.
- El manifiesto se lee progresivamente con `DeserializeAsyncEnumerable`.
- Solo se mantienen ventanas pequeñas de documentos.
- Las descargas se escriben directamente a disco.
- Los PDF se rasterizan utilizando archivos temporales.
- Los resultados se persisten documento por documento.
- Los intentos SQLite se guardan cuando terminan.
- Los temporales se eliminan después de que el resultado alcanza `Completed`.

## Robustez

Los archivos críticos se escriben de forma atómica cuando corresponde:

```text
archivo.tmp
    ↓
flush
    ↓
rename
    ↓
archivo final
```

La aplicación prioriza recuperarse de errores en lugar de realizar validaciones locales redundantes.

Los errores de SQL Server se delegan al motor cuando este ya puede determinar correctamente la validez de la operación. Los errores de infraestructura y VLM se administran dentro del balanceador.

## Criterio de éxito

El diseño busca mantener este flujo incluso con selecciones de miles de registros:

```text
selección exacta
    ↓
descarga acotada
    ↓
distribución VLM
    ↓
resultado local
    ↓
historial SQLite
    ↓
persistencia SQL idempotente
    ↓
liberación de temporal
    ↓
siguiente documento
```

Y después de una caída:

```text
reiniciar VlmHub
    ↓
recuperar procesamiento
    ↓
no repetir Completed/PendingUpload
    ↓
conservar resultados terminados
    ↓
continuar trabajo pendiente
```

## Archivos importantes

```text
VlmHub.Console/App/VlmHubConsoleApp.cs
VlmHub.Console/Processing/ProcessingEngine.cs
VlmHub.Console/Processing/ProcessingWorker.cs
VlmHub.Console/Processing/BackgroundProcessingLauncher.cs
VlmHub.Console/Processing/ProcessingRecovery.cs

VlmHub.Sql/SqlServer.cs
VlmHub.Sql/Persistence/LocalHistoryStore.cs

VlmHub.Balancer/Orchestration/ProcessingCoordinator.cs
VlmHub.Balancer/Orchestration/DocumentPreprocessor.cs
VlmHub.Balancer/Orchestration/ResultWriter.cs
VlmHub.Balancer/Orchestration/VlmContentSanitizer.cs
```

## Nota de despliegue

Antes de desplegar una nueva versión se recomienda ejecutar:

```bash
dotnet restore VlmHub.sln
dotnet build VlmHub.sln -c Release
```

y realizar una prueba mínima con:

1. una imagen;
2. un PDF de varias páginas;
3. una interrupción durante VLM;
4. una interrupción después del resultado local y antes de SQL;
5. una recuperación con `PendingUpload`;
6. más de un VLM Server disponible.
