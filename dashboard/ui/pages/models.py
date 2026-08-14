from __future__ import annotations

from dash import html

from services.metrics import model_summary
from ui.components import (
    data_grid,
    graph_card,
    page_title,
)
from ui.figures import (
    attempts_over_time_figure,
    model_avg_time_figure,
    model_success_figure,
    model_time_box_figure,
)


def render(documents, units, summary):
    model_data = model_summary(units).round(3)

    columns = [
        {"field": "Model", "headerName": "Modelo", "flex": 2},
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
                "Modelos",
                (
                    "Éxito, latencia, dispersión y actividad "
                    "de cada modelo VLM."
                ),
            ),
            html.Section(
                className="grid grid-2",
                children=[
                    graph_card(model_success_figure(units)),
                    graph_card(model_avg_time_figure(units)),
                    graph_card(model_time_box_figure(units)),
                    graph_card(attempts_over_time_figure(units)),
                ],
            ),
            html.Div(
                className="section-title",
                children=[
                    html.H2("Detalle por modelo"),
                    html.P(
                        "Estadísticas agregadas de los intentos procesados."
                    ),
                ],
            ),
            html.Div(
                className="panel table-panel",
                children=[
                    data_grid(
                        model_data.to_dict("records"),
                        columns,
                        height="500px",
                        page_size=15,
                    )
                ],
            ),
        ]
    )
