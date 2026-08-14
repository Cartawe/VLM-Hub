from __future__ import annotations

from dash import html

from ui.components import (
    data_grid,
    graph_card,
    page_title,
)
from ui.figures import (
    document_time_histogram,
    slow_documents_figure,
)


def render(documents, units, summary):
    data = summary.copy()

    if not data.empty:
        data["FinishedAt"] = (
            data["FinishedAt"]
            .dt.strftime("%d-%m-%Y %H:%M:%S")
        )
        data["StartedAt"] = (
            data["StartedAt"]
            .dt.strftime("%d-%m-%Y %H:%M:%S")
        )

    columns = [
        {"field": "DocumentId", "headerName": "Id"},
        {
            "field": "ProcessingId",
            "headerName": "ProcessingId",
            "flex": 2,
        },
        {
            "field": "OriginalKey",
            "headerName": "OriginalKey",
            "flex": 1,
        },
        {
            "field": "DocumentName",
            "headerName": "Documento",
            "flex": 2,
        },
        {"field": "DatabaseName", "headerName": "BD"},
        {"field": "SourceTable", "headerName": "Tabla"},
        {"field": "Status", "headerName": "Resultado"},
        {
            "field": "DocumentProcessingTime",
            "headerName": "Tiempo (s)",
            "valueFormatter": {
                "function": (
                    "params.value == null ? '' : "
                    "params.value.toFixed(3)"
                )
            },
        },
        {"field": "Units", "headerName": "Unidades"},
        {"field": "Attempts", "headerName": "Intentos"},
        {
            "field": "FailedAttempts",
            "headerName": "Intentos fallidos",
        },
        {"field": "FinishedAt", "headerName": "Finalizado"},
    ]

    return html.Div(
        children=[
            page_title(
                "Documentos",
                (
                    "Tiempos, documentos lentos y detalle "
                    "de cada ejecución documental finalizada."
                ),
            ),
            html.Section(
                className="grid grid-2",
                children=[
                    graph_card(document_time_histogram(documents)),
                    graph_card(slow_documents_figure(summary)),
                ],
            ),
            html.Div(
                className="section-title",
                children=[
                    html.H2("Documentos procesados"),
                    html.P(
                        "La tabla no incluye documentos todavía en curso."
                    ),
                ],
            ),
            html.Div(
                className="panel table-panel",
                children=[
                    data_grid(
                        data.to_dict("records"),
                        columns,
                        height="680px",
                        page_size=30,
                    )
                ],
            ),
        ]
    )
