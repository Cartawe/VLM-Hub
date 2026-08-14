from __future__ import annotations

from dash import html

from services.metrics import (
    execution_parallel_summary,
)
from ui.components import (
    data_grid,
    graph_card,
    page_title,
)
from ui.figures import (
    execution_parallel_figure,
)


def render(documents, units, summary):
    executions = execution_parallel_summary(
        documents,
        units,
    ).copy()

    if not executions.empty:
        executions["ParallelHours"] = (
            executions["ParallelSeconds"] / 3600
        )
        executions["SerialHours"] = (
            executions["SerialSeconds"] / 3600
        )
        executions["SavedHours"] = (
            executions["SavedSeconds"] / 3600
        )
        executions["StartedAt"] = (
            executions["StartedAt"]
            .dt.strftime("%d-%m-%Y %H:%M:%S")
        )
        executions["FinishedAt"] = (
            executions["FinishedAt"]
            .dt.strftime("%d-%m-%Y %H:%M:%S")
        )

    columns = [
        {
            "field": "ProcessingId",
            "headerName": "ProcessingId",
            "flex": 2,
        },
        {
            "field": "Documents",
            "headerName": "Documentos",
        },
        {
            "field": "Attempts",
            "headerName": "Intentos",
        },
        {
            "field": "Servers",
            "headerName": "Servidores",
        },
        {
            "field": "ParallelHours",
            "headerName": "Paralelo (h)",
            "valueFormatter": {
                "function": (
                    "params.value == null ? '' : "
                    "params.value.toFixed(3)"
                )
            },
        },
        {
            "field": "SerialHours",
            "headerName": "1 servidor (h)",
            "valueFormatter": {
                "function": (
                    "params.value == null ? '' : "
                    "params.value.toFixed(3)"
                )
            },
        },
        {
            "field": "Speedup",
            "headerName": "Speedup",
            "valueFormatter": {
                "function": (
                    "params.value == null ? '' : "
                    "params.value.toFixed(2) + '×'"
                )
            },
        },
        {
            "field": "SavedPercent",
            "headerName": "Ahorro %",
            "valueFormatter": {
                "function": (
                    "params.value == null ? '' : "
                    "params.value.toFixed(1) + '%'"
                )
            },
        },
        {
            "field": "StartedAt",
            "headerName": "Inicio VLM estimado",
        },
        {
            "field": "FinishedAt",
            "headerName": "Fin VLM",
        },
    ]

    return html.Div(
        children=[
            page_title(
                "Ejecuciones",
                (
                    "Detalle de la ganancia obtenida por paralelismo "
                    "para cada ProcessingId."
                ),
            ),
            graph_card(
                execution_parallel_figure(
                    documents,
                    units,
                )
            ),
            html.Div(
                className="section-title",
                children=[
                    html.H2("Detalle por ProcessingId"),
                    html.P(
                        "Comparación entre tiempo observado y "
                        "procesamiento secuencial teórico."
                    ),
                ],
            ),
            html.Div(
                className="panel table-panel",
                children=[
                    data_grid(
                        executions.to_dict("records"),
                        columns,
                        height="620px",
                        page_size=20,
                    )
                ],
            ),
        ]
    )
