from __future__ import annotations

import dash_ag_grid as dag
from dash import dcc, html

from services.metrics import (
    DashboardMetrics,
    ParallelMetrics,
    format_seconds,
)


NAV_ITEMS = [
    ("/", "Resumen", "nav-summary"),
    ("/ejecuciones", "Ejecuciones", "nav-executions"),
    ("/modelos", "Modelos", "nav-models"),
    ("/servidores", "Servidores", "nav-servers"),
    ("/documentos", "Documentos", "nav-documents"),
    ("/errores", "Errores", "nav-errors"),
]


def header():
    return html.Div(
        className="top-shell",
        children=[
            html.Div(
                className="brand-area",
                children=[
                    html.Div("VLMHUB", className="brand-kicker"),
                    html.Div("Processing Analytics", className="brand-title"),
                    html.Div(
                        "Historial SQLite de procesamientos VLM finalizados",
                        className="brand-subtitle",
                    ),
                ],
            ),
            html.Nav(
                className="nav",
                children=[
                    dcc.Link(
                        label,
                        href=href,
                        id=nav_id,
                        className="nav-link",
                    )
                    for href, label, nav_id in NAV_ITEMS
                ],
            ),
            html.Div(
                className="header-status",
                children=[
                    html.Span("SQLite", className="status-pill"),
                    html.Span(id="last-refresh", className="refresh-label"),
                ],
            ),
        ],
    )


def filters():
    return html.Section(
        className="filters",
        children=[
            html.Div(
                className="filter",
                children=[
                    html.Label("Rango de fechas"),
                    dcc.DatePickerRange(
                        id="date-filter",
                        display_format="DD-MM-YYYY",
                        clearable=True,
                    ),
                ],
            ),
            html.Div(
                className="filter",
                children=[
                    html.Label("Modelo"),
                    dcc.Dropdown(
                        id="model-filter",
                        multi=True,
                        placeholder="Todos los modelos",
                    ),
                ],
            ),
            html.Div(
                className="filter",
                children=[
                    html.Label("Servidor VLM"),
                    dcc.Dropdown(
                        id="server-filter",
                        multi=True,
                        placeholder="Todos los servidores",
                    ),
                ],
            ),
            html.Div(
                className="filter",
                children=[
                    html.Label("Finish reason"),
                    dcc.Dropdown(
                        id="finish-reason-filter",
                        multi=True,
                        placeholder="Todos",
                    ),
                ],
            ),
            html.Div(
                className="filter filter-search",
                children=[
                    html.Label("Buscar documento"),
                    dcc.Input(
                        id="search-filter",
                        type="text",
                        placeholder="Nombre, OriginalKey, ProcessingId...",
                        debounce=True,
                    ),
                ],
            ),
        ],
    )


def page_title(title: str, subtitle: str):
    return html.Div(
        className="page-title",
        children=[
            html.H1(title),
            html.P(subtitle),
        ],
    )


def graph_card(figure, class_name: str = ""):
    return html.Div(
        className=f"panel graph-panel {class_name}".strip(),
        children=[
            dcc.Graph(
                figure=figure,
                config={
                    "displaylogo": False,
                    "responsive": True,
                },
            )
        ],
    )


def kpi_card(
    title: str,
    value: str,
    subtitle: str,
    tone: str,
):
    return html.Div(
        className=f"kpi-card kpi-{tone}",
        children=[
            html.Div(title, className="kpi-title"),
            html.Div(value, className="kpi-value"),
            html.Div(subtitle, className="kpi-subtitle"),
        ],
    )


def overview_kpis(
    metrics: DashboardMetrics,
    parallel: ParallelMetrics,
):
    return [
        kpi_card(
            "Documentos procesados",
            f"{metrics.total_documents:,}",
            f"{metrics.successful_documents:,} correctos",
            "blue",
        ),
        kpi_card(
            "Tasa de éxito",
            f"{metrics.success_rate:.1f}%",
            f"{metrics.failed_documents:,} con error",
            "green",
        ),
        kpi_card(
            "Tiempo promedio",
            format_seconds(metrics.avg_document_time),
            f"Mediana {format_seconds(metrics.median_document_time)}",
            "cyan",
        ),
        kpi_card(
            "P95 documento",
            format_seconds(metrics.p95_document_time),
            "95% termina bajo este tiempo",
            "purple",
        ),
        kpi_card(
            "Tiempo paralelo",
            format_seconds(parallel.parallel_seconds),
            f"{parallel.executions:,} ejecuciones",
            "orange",
        ),
        kpi_card(
            "1 servidor teórico",
            format_seconds(parallel.serial_seconds),
            (
                f"Speedup {parallel.speedup:.2f}× · "
                f"ahorro {parallel.saved_percent:.1f}%"
            ),
            "pink",
        ),
    ]


def data_grid(
    row_data,
    column_defs,
    height: str = "620px",
    page_size: int = 25,
):
    return dag.AgGrid(
        className="ag-theme-quartz vlm-grid",
        columnDefs=column_defs,
        rowData=row_data,
        dashGridOptions={
            "pagination": True,
            "paginationPageSize": page_size,
            "animateRows": False,
        },
        defaultColDef={
            "sortable": True,
            "filter": True,
            "resizable": True,
        },
        style={"height": height, "width": "100%"},
    )


def note_card(title: str, body: str):
    return html.Div(
        className="note-card",
        children=[
            html.Div(title, className="note-title"),
            html.Div(body, className="note-body"),
        ],
    )


def empty_state(text: str):
    return html.Div(
        text,
        className="empty-state",
    )
