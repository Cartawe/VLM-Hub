from __future__ import annotations

from dash import html

from services.metrics import (
    calculate_metrics,
    calculate_parallel_metrics,
    format_seconds,
)
from ui.components import (
    graph_card,
    note_card,
    overview_kpis,
    page_title,
)
from ui.figures import (
    document_status_figure,
    parallel_serial_figure,
    timeline_figure,
)


def render(documents, units, summary):
    metrics = calculate_metrics(
        documents,
        units,
    )
    parallel = calculate_parallel_metrics(
        documents,
        units,
    )

    difference_text = (
        f"El paralelismo reduce el tiempo teórico en "
        f"{parallel.saved_percent:.1f}% "
        f"({format_seconds(parallel.saved_seconds)})"
        if parallel.serial_seconds > 0
        else "No hay tiempos suficientes para calcular la comparación."
    )

    return html.Div(
        children=[
            page_title(
                "Resumen",
                (
                    "Vista ejecutiva del rendimiento de los documentos "
                    "ya procesados por VlmHub."
                ),
            ),
            html.Section(
                className="kpi-grid",
                children=overview_kpis(
                    metrics,
                    parallel,
                ),
            ),
            html.Section(
                className="hero-comparison",
                children=[
                    graph_card(
                        parallel_serial_figure(
                            documents,
                            units,
                        ),
                        "comparison-panel",
                    ),
                    html.Div(
                        className="comparison-summary",
                        children=[
                            html.Div(
                                "Impacto del paralelismo",
                                className="comparison-kicker",
                            ),
                            html.Div(
                                f"{parallel.speedup:.2f}×",
                                className="comparison-value",
                            ),
                            html.Div(
                                "speedup observado",
                                className="comparison-label",
                            ),
                            html.Div(
                                difference_text,
                                className="comparison-description",
                            ),
                            html.Div(
                                [
                                    html.Span(
                                        f"{parallel.servers} servidores",
                                        className="mini-pill mini-blue",
                                    ),
                                    html.Span(
                                        f"{metrics.attempts} intentos",
                                        className="mini-pill mini-purple",
                                    ),
                                    html.Span(
                                        f"{metrics.retry_rate:.1f}% reintentos",
                                        className="mini-pill mini-orange",
                                    ),
                                ],
                                className="mini-pills",
                            ),
                        ],
                    ),
                ],
            ),
            note_card(
                "Cómo se calcula la comparación",
                (
                    "“1 servidor teórico” suma ProcessingTime de todos los "
                    "intentos de ProcessingUnit. “Paralelo observado” mide, "
                    "por ProcessingId, desde el inicio inferido del primer "
                    "intento (ProcessedAt - ProcessingTime) hasta el final "
                    "del último intento. De esta forma la comparación usa "
                    "solo tiempo VLM registrado y no incluye documentos "
                    "todavía en proceso."
                ),
            ),
            html.Section(
                className="grid grid-2",
                children=[
                    graph_card(
                        document_status_figure(
                            documents
                        )
                    ),
                    graph_card(
                        timeline_figure(
                            documents
                        )
                    ),
                ],
            ),
        ]
    )
