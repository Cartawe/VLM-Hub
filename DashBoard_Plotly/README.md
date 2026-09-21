# VlmHub Processing Analytics v2

Dashboard modular para analizar el historial SQLite de VlmHub.

## Cambios principales de esta versión

- Nueva paleta clara de alto contraste.
- Corrección explícita del contraste de:
  - Plotly;
  - Dash Dropdown;
  - Dash DatePicker;
  - Dash AG Grid.
- Se eliminó el filtro de estado.
- Se analizan exclusivamente documentos con:

```sql
FinishedAt IS NOT NULL
```

- Se separaron los detalles en páginas:
  - Resumen
  - Ejecuciones
  - Modelos
  - Servidores
  - Documentos
  - Errores
- Se agregó comparación:
  - tiempo paralelo observado;
  - tiempo teórico con un solo servidor.

## Comparación paralelo vs. un servidor

La comparación se construye con `ProcessingUnit`.

Para cada intento:

```text
inicio estimado = ProcessedAt - ProcessingTime
fin             = ProcessedAt
```

Por cada `ProcessingId`:

```text
Paralelo observado =
    último fin - primer inicio
```

El escenario de un solo servidor es:

```text
1 servidor teórico =
    suma de ProcessingUnit.ProcessingTime
```

Por lo tanto, el escenario serial representa ejecutar todos los intentos
uno detrás de otro.

El dashboard también calcula:

```text
speedup = serial / paralelo
ahorro  = serial - paralelo
```

## Esquema SQLite esperado

### Document

- Id
- ProcessingId
- ServerHost
- DatabaseName
- SourceTable
- OriginalKey
- DocumentName
- ServerProcessingId
- Success
- ProcessingTime
- StartedAt
- FinishedAt

### ProcessingUnit

- Id
- DocumentId
- UnitIndex
- AttemptNumber
- Model
- Server
- Success
- FinishReason
- ProcessingTime
- ProcessedAt
- Transcription
- Error

`Transcription` no se carga en memoria porque no es necesaria para las métricas.

## Instalar

```bash
python3 -m venv .venv_dashboard
source .venv_dashboard/bin/activate

pip install -r requirements.txt
```

## Ejecutar

```bash
VLMHUB_DB=/home/ocr/Desktop/VlmHub_v1.2/data/vlmhub.db python app.py
```

Abrir:

```text
http://127.0.0.1:8050
```

## Acceder desde otros equipos

```bash
VLMHUB_DASHBOARD_HOST=0.0.0.0 \
VLMHUB_DB=/home/ocr/Desktop/VlmHub_v1.2/data/vlmhub.db \
python app.py
```

## Actualización

Por defecto el dashboard vuelve a leer SQLite cada 30 segundos.

```bash
VLMHUB_DASHBOARD_REFRESH=10 \
VLMHUB_DB=/home/ocr/Desktop/VlmHub_v1.2/data/vlmhub.db \
python app.py
```
