from __future__ import annotations

from dash import html

from services.metrics import failed_attempts_detail
from ui.components import (
    data_grid,
    graph_card,
    page_title,
)
from ui.figures import (
    errors_figure,
    finish_reason_figure,
    retry_figure,
)


def render(documents, units, summary):
    failures = failed_attempts_detail(
        documents,
        units,
    ).copy()

    if not failures.empty:
        failures["ProcessedAt"] = (
            failures["ProcessedAt"]
            .dt.strftime("%d-%m-%Y %H:%M:%S")
        )

    columns = [
        {"field": "ProcessingId", "headerName": "ProcessingId", "flex": 2},
        {"field": "DocumentName", "headerName": "Documento", "flex": 2},
        {"field": "OriginalKey", "headerName": "OriginalKey"},
        {"field": "UnitIndex", "headerName": "Unidad"},
        {"field": "AttemptNumber", "headerName": "Intento"},
        {"field": "Model", "headerName": "Modelo", "flex": 1},
        {"field": "Server", "headerName": "Servidor", "flex": 1},
        {"field": "FinishReason", "headerName": "Finish reason"},
        {
            "field": "ProcessingTime",
            "headerName": "Tiempo (s)",
            "valueFormatter": {
                "function": (
                    "params.value == null ? '' : "
                    "params.value.toFixed(3)"
                )
            },
        },
        {"field": "ProcessedAt", "headerName": "Procesado"},
        {"field": "Error", "headerName": "Error", "flex": 3},
    ]

    return html.Div(
        children=[
            page_title(
                "Errores y reintentos",
                (
                    "Diagnóstico de finish_reason, intentos repetidos "
                    "y fallos registrados por las unidades."
                ),
            ),
            html.Section(
                className="grid grid-3",
                children=[
                    graph_card(finish_reason_figure(units)),
                    graph_card(retry_figure(units)),
                    graph_card(errors_figure(units)),
                ],
            ),
            html.Div(
                className="section-title",
                children=[
                    html.H2("Intentos fallidos"),
                    html.P(
                        "Detalle de ProcessingUnit para diagnóstico."
                    ),
                ],
            ),
            html.Div(
                className="panel table-panel",
                children=[
                    data_grid(
                        failures.to_dict("records"),
                        columns,
                        height="680px",
                        page_size=30,
                    )
                ],
            ),
        ]
    )
