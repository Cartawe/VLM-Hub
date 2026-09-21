from __future__ import annotations

from dash import html

from services.metrics import server_summary
from ui.components import (
    data_grid,
    graph_card,
    page_title,
)
from ui.figures import (
    server_avg_time_figure,
    server_success_figure,
    server_workload_figure,
)


def render(documents, units, summary):
    server_data = server_summary(units).round(3)

    columns = [
        {"field": "Server", "headerName": "Servidor", "flex": 2},
        {"field": "Attempts", "headerName": "Intentos"},
        {"field": "Successes", "headerName": "Éxitos"},
        {"field": "Failures", "headerName": "Errores"},
        {
            "field": "SuccessRate",
            "headerName": "Éxito %",
            "valueFormatter": {
                "function": (
                    "params.value == null ? '' : "
                    "params.value.toFixed(1) + '%'"
                )
            },
        },
        {
            "field": "AvgTime",
            "headerName": "Promedio (s)",
            "valueFormatter": {
                "function": (
                    "params.value == null ? '' : "
                    "params.value.toFixed(2)"
                )
            },
        },
        {
            "field": "MedianTime",
            "headerName": "Mediana (s)",
            "valueFormatter": {
                "function": (
                    "params.value == null ? '' : "
                    "params.value.toFixed(2)"
                )
            },
        },
        {
            "field": "P95Time",
            "headerName": "P95 (s)",
            "valueFormatter": {
                "function": (
                    "params.value == null ? '' : "
                    "params.value.toFixed(2)"
                )
            },
        },
    ]

    return html.Div(
        children=[
            page_title(
                "Servidores",
                (
                    "Carga distribuida, éxito y tiempos "
                    "de los VLM Servers."
                ),
            ),
            html.Section(
                className="grid grid-3",
                children=[
                    graph_card(server_success_figure(units)),
                    graph_card(server_avg_time_figure(units)),
                    graph_card(server_workload_figure(units)),
                ],
            ),
            html.Div(
                className="section-title",
                children=[
                    html.H2("Detalle por servidor"),
                ],
            ),
            html.Div(
                className="panel table-panel",
                children=[
                    data_grid(
                        server_data.to_dict("records"),
                        columns,
                        height="500px",
                        page_size=15,
                    )
                ],
            ),
        ]
    )
