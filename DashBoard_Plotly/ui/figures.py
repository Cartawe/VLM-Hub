from __future__ import annotations

import pandas as pd
import plotly.express as px
import plotly.graph_objects as go

from services.metrics import (
    error_summary,
    execution_parallel_summary,
    finish_reason_summary,
    model_summary,
    processing_timeline,
    server_summary,
)


PALETTE = [
    "#2563EB",
    "#16A34A",
    "#D97706",
    "#7C3AED",
    "#0891B2",
    "#DB2777",
    "#EA580C",
    "#0F766E",
    "#DC2626",
    "#4F46E5",
]

TEXT = "#172033"
MUTED = "#64748B"
GRID = "#E2E8F0"
WHITE = "#FFFFFF"


def empty_figure_message(text: str):
    fig = go.Figure()
    fig.add_annotation(
        text=text,
        x=0.5,
        y=0.5,
        xref="paper",
        yref="paper",
        showarrow=False,
        font={"size": 16, "color": MUTED},
    )
    fig.update_xaxes(visible=False)
    fig.update_yaxes(visible=False)
    return style_figure(fig, "")


def style_figure(fig, title: str):
    fig.update_layout(
        title={
            "text": title,
            "font": {
                "size": 18,
                "color": TEXT,
            },
            "x": 0.03,
            "xanchor": "left",
        },
        font={
            "family": (
                "Inter, ui-sans-serif, system-ui, "
                "-apple-system, BlinkMacSystemFont, Segoe UI, sans-serif"
            ),
            "size": 13,
            "color": TEXT,
        },
        colorway=PALETTE,
        margin=dict(l=55, r=28, t=70, b=55),
        paper_bgcolor=WHITE,
        plot_bgcolor=WHITE,
        legend=dict(
            font={"color": TEXT},
            bgcolor="rgba(255,255,255,0.90)",
        ),
        hoverlabel=dict(
            bgcolor="#0F172A",
            bordercolor="#334155",
            font=dict(
                color="#FFFFFF",
                size=13,
            ),
            align="left",
        ),
    )

    fig.update_xaxes(
        title_font={"color": TEXT},
        tickfont={"color": TEXT},
        gridcolor=GRID,
        zerolinecolor=GRID,
        linecolor="#CBD5E1",
    )
    fig.update_yaxes(
        title_font={"color": TEXT},
        tickfont={"color": TEXT},
        gridcolor=GRID,
        zerolinecolor=GRID,
        linecolor="#CBD5E1",
    )

    return fig


def document_status_figure(documents: pd.DataFrame):
    if documents.empty:
        return empty_figure_message("Sin documentos procesados")

    status = (
        documents["Status"]
        .value_counts()
        .rename_axis("Estado")
        .reset_index(name="Documentos")
    )

    fig = px.pie(
        status,
        names="Estado",
        values="Documentos",
        hole=0.62,
        color="Estado",
        color_discrete_map={
            "Éxito": "#16A34A",
            "Error": "#DC2626",
        },
    )

    fig.update_traces(
        textfont_color=TEXT,
        textinfo="percent+label",
    )

    return style_figure(
        fig,
        "Resultado de documentos procesados",
    )


def document_time_histogram(documents: pd.DataFrame):
    df = documents.dropna(
        subset=["ProcessingTime"]
    ).copy()

    if df.empty:
        return empty_figure_message("Sin tiempos de documento")

    fig = px.histogram(
        df,
        x="ProcessingTime",
        nbins=min(45, max(10, int(len(df) ** 0.5))),
        labels={
            "ProcessingTime": "Tiempo (s)",
            "count": "Documentos",
        },
        color_discrete_sequence=["#2563EB"],
    )

    return style_figure(
        fig,
        "Distribución de tiempo por documento",
    )


def timeline_figure(documents: pd.DataFrame):
    timeline = processing_timeline(documents)

    if timeline.empty:
        return empty_figure_message("Sin fechas de procesamiento")

    long = timeline.melt(
        id_vars=["Period"],
        value_vars=["Successes", "Failures"],
        var_name="Estado",
        value_name="Documentos",
    )

    long["Estado"] = long["Estado"].map(
        {
            "Successes": "Éxito",
            "Failures": "Error",
        }
    )

    fig = px.bar(
        long,
        x="Period",
        y="Documentos",
        color="Estado",
        barmode="stack",
        color_discrete_map={
            "Éxito": "#16A34A",
            "Error": "#DC2626",
        },
        labels={
            "Period": "Hora",
            "Documentos": "Documentos finalizados",
        },
    )

    return style_figure(
        fig,
        "Throughput de documentos finalizados",
    )


def parallel_serial_figure(
    documents: pd.DataFrame,
    units: pd.DataFrame,
):
    summary = execution_parallel_summary(
        documents,
        units,
    )

    if summary.empty:
        return empty_figure_message(
            "No hay suficientes tiempos de ProcessingUnit"
        )

    parallel_seconds = summary["ParallelSeconds"].sum()
    serial_seconds = summary["SerialSeconds"].sum()

    data = pd.DataFrame(
        {
            "Escenario": [
                "Paralelo observado",
                "1 servidor teórico",
            ],
            "Horas": [
                parallel_seconds / 3600,
                serial_seconds / 3600,
            ],
        }
    )

    fig = px.bar(
        data,
        x="Escenario",
        y="Horas",
        text="Horas",
        color="Escenario",
        color_discrete_map={
            "Paralelo observado": "#2563EB",
            "1 servidor teórico": "#D97706",
        },
        labels={"Horas": "Tiempo (h)"},
    )

    fig.update_traces(
        texttemplate="%{text:.2f} h",
        textposition="outside",
    )

    return style_figure(
        fig,
        "Tiempo real paralelo vs. un solo servidor",
    )


def execution_parallel_figure(
    documents: pd.DataFrame,
    units: pd.DataFrame,
    max_executions: int = 18,
):
    summary = execution_parallel_summary(
        documents,
        units,
    )

    if summary.empty:
        return empty_figure_message(
            "Sin ejecuciones comparables"
        )

    df = summary.head(max_executions).copy()
    df = df.sort_values("FinishedAt")

    df["Ejecución"] = df["ProcessingId"].map(
        lambda value: (
            value[:10] + "…"
            if len(value) > 11
            else value
        )
    )
    df["Paralelo"] = df["ParallelSeconds"] / 3600
    df["Serial"] = df["SerialSeconds"] / 3600

    long = df.melt(
        id_vars=["Ejecución"],
        value_vars=["Paralelo", "Serial"],
        var_name="Escenario",
        value_name="Horas",
    )

    long["Escenario"] = long["Escenario"].map(
        {
            "Paralelo": "Paralelo observado",
            "Serial": "1 servidor teórico",
        }
    )

    fig = px.bar(
        long,
        x="Ejecución",
        y="Horas",
        color="Escenario",
        barmode="group",
        color_discrete_map={
            "Paralelo observado": "#2563EB",
            "1 servidor teórico": "#D97706",
        },
        labels={"Horas": "Tiempo (h)"},
    )

    return style_figure(
        fig,
        "Comparación por ProcessingId",
    )


def model_success_figure(units: pd.DataFrame):
    summary = model_summary(units)

    if summary.empty:
        return empty_figure_message("Sin datos de modelos")

    fig = px.bar(
        summary,
        x="Model",
        y="SuccessRate",
        text="SuccessRate",
        color="Model",
        color_discrete_sequence=PALETTE,
        hover_data=[
            "Attempts",
            "Successes",
            "Failures",
        ],
        labels={
            "Model": "Modelo",
            "SuccessRate": "Éxito (%)",
        },
    )

    fig.update_traces(
        texttemplate="%{text:.1f}%",
        textposition="outside",
    )
    fig.update_yaxes(range=[0, 105])
    fig.update_layout(showlegend=False)

    return style_figure(
        fig,
        "Tasa de éxito por modelo",
    )


def model_avg_time_figure(units: pd.DataFrame):
    summary = model_summary(units)

    if summary.empty:
        return empty_figure_message("Sin datos de modelos")

    fig = px.bar(
        summary.sort_values("AvgTime"),
        x="Model",
        y="AvgTime",
        color="Model",
        color_discrete_sequence=PALETTE,
        hover_data=[
            "MedianTime",
            "P95Time",
            "Attempts",
        ],
        labels={
            "Model": "Modelo",
            "AvgTime": "Tiempo promedio (s)",
        },
    )

    fig.update_layout(showlegend=False)

    return style_figure(
        fig,
        "Tiempo promedio por intento y modelo",
    )


def model_time_box_figure(units: pd.DataFrame):
    df = units.dropna(
        subset=["ProcessingTime"]
    ).copy()

    if df.empty:
        return empty_figure_message("Sin tiempos por modelo")

    fig = px.box(
        df,
        x="Model",
        y="ProcessingTime",
        color="Model",
        color_discrete_sequence=PALETTE,
        points="outliers",
        labels={
            "Model": "Modelo",
            "ProcessingTime": "Tiempo (s)",
        },
    )

    fig.update_layout(showlegend=False)

    return style_figure(
        fig,
        "Dispersión y outliers de tiempo por modelo",
    )


def attempts_over_time_figure(units: pd.DataFrame):
    df = units.dropna(
        subset=["ProcessedAt"]
    ).copy()

    if df.empty:
        return empty_figure_message("Sin fechas de intentos")

    df["Period"] = df["ProcessedAt"].dt.floor("h")

    grouped = (
        df.groupby(
            ["Period", "Model"],
            dropna=False,
        )
        .size()
        .reset_index(name="Attempts")
    )

    fig = px.line(
        grouped,
        x="Period",
        y="Attempts",
        color="Model",
        markers=True,
        color_discrete_sequence=PALETTE,
        labels={
            "Period": "Hora",
            "Attempts": "Intentos",
            "Model": "Modelo",
        },
    )

    return style_figure(
        fig,
        "Actividad de modelos en el tiempo",
    )


def server_success_figure(units: pd.DataFrame):
    summary = server_summary(units)

    if summary.empty:
        return empty_figure_message("Sin datos de servidores VLM")

    fig = px.bar(
        summary,
        x="Server",
        y="SuccessRate",
        text="SuccessRate",
        color="Server",
        color_discrete_sequence=PALETTE,
        hover_data=[
            "Attempts",
            "AvgTime",
            "P95Time",
        ],
        labels={
            "Server": "Servidor",
            "SuccessRate": "Éxito (%)",
        },
    )

    fig.update_traces(
        texttemplate="%{text:.1f}%",
        textposition="outside",
    )
    fig.update_yaxes(range=[0, 105])
    fig.update_layout(showlegend=False)

    return style_figure(
        fig,
        "Tasa de éxito por servidor VLM",
    )


def server_avg_time_figure(units: pd.DataFrame):
    summary = server_summary(units)

    if summary.empty:
        return empty_figure_message("Sin datos de servidores VLM")

    fig = px.bar(
        summary.sort_values("AvgTime"),
        x="Server",
        y="AvgTime",
        color="Server",
        color_discrete_sequence=PALETTE,
        hover_data=[
            "Attempts",
            "MedianTime",
            "P95Time",
        ],
        labels={
            "Server": "Servidor",
            "AvgTime": "Tiempo promedio (s)",
        },
    )

    fig.update_layout(showlegend=False)

    return style_figure(
        fig,
        "Tiempo promedio por servidor",
    )


def server_workload_figure(units: pd.DataFrame):
    if units.empty:
        return empty_figure_message("Sin datos de servidores VLM")

    workload = (
        units["Server"]
        .value_counts()
        .rename_axis("Server")
        .reset_index(name="Attempts")
    )

    fig = px.pie(
        workload,
        names="Server",
        values="Attempts",
        hole=0.5,
        color="Server",
        color_discrete_sequence=PALETTE,
    )

    fig.update_traces(
        textfont_color=TEXT,
        textinfo="percent+label",
    )

    return style_figure(
        fig,
        "Distribución de carga entre servidores",
    )


def finish_reason_figure(units: pd.DataFrame):
    summary = finish_reason_summary(units)

    if summary.empty:
        return empty_figure_message("Sin finish_reason")

    fig = px.bar(
        summary,
        x="FinishReason",
        y="Count",
        color="FinishReason",
        color_discrete_sequence=PALETTE,
        labels={
            "FinishReason": "Finish reason",
            "Count": "Intentos",
        },
    )

    fig.update_layout(showlegend=False)

    return style_figure(
        fig,
        "Distribución de finish_reason",
    )


def retry_figure(units: pd.DataFrame):
    if units.empty:
        return empty_figure_message("Sin intentos")

    counts = (
        units.groupby(
            ["DocumentId", "UnitIndex"],
            dropna=False,
        )
        .size()
        .reset_index(name="Attempts")
    )

    distribution = (
        counts["Attempts"]
        .value_counts()
        .sort_index()
        .rename_axis("Attempts")
        .reset_index(name="Units")
    )

    fig = px.bar(
        distribution,
        x="Attempts",
        y="Units",
        color="Attempts",
        color_continuous_scale=[
            [0.0, "#16A34A"],
            [0.5, "#D97706"],
            [1.0, "#DC2626"],
        ],
        labels={
            "Attempts": "Intentos por unidad",
            "Units": "Unidades",
        },
    )

    fig.update_layout(coloraxis_showscale=False)

    return style_figure(
        fig,
        "Reintentos requeridos por unidad",
    )


def errors_figure(units: pd.DataFrame):
    summary = error_summary(units)

    if summary.empty:
        return empty_figure_message("No hay errores registrados")

    fig = px.bar(
        summary.sort_values("Count"),
        x="Count",
        y="Error",
        orientation="h",
        color="Count",
        color_continuous_scale=[
            [0.0, "#FCA5A5"],
            [1.0, "#B91C1C"],
        ],
        labels={
            "Count": "Ocurrencias",
        },
    )

    fig.update_layout(coloraxis_showscale=False)

    return style_figure(
        fig,
        "Errores más frecuentes",
    )


def slow_documents_figure(summary: pd.DataFrame):
    if summary.empty:
        return empty_figure_message("Sin documentos")

    df = summary.dropna(
        subset=["DocumentProcessingTime"]
    ).copy()

    if df.empty:
        return empty_figure_message("Sin tiempos de documento")

    df = (
        df.nlargest(
            15,
            "DocumentProcessingTime",
        )
        .sort_values(
            "DocumentProcessingTime"
        )
    )

    label = (
        df["DocumentName"]
        .fillna("")
        .astype(str)
    )

    label = label.where(
        label.str.len() > 0,
        df["OriginalKey"].astype(str),
    )

    df["Label"] = label

    fig = px.bar(
        df,
        x="DocumentProcessingTime",
        y="Label",
        orientation="h",
        color="DocumentProcessingTime",
        color_continuous_scale=[
            [0.0, "#60A5FA"],
            [0.5, "#8B5CF6"],
            [1.0, "#DB2777"],
        ],
        hover_data=[
            "OriginalKey",
            "Units",
            "Attempts",
            "FailedAttempts",
        ],
        labels={
            "DocumentProcessingTime": "Tiempo (s)",
            "Label": "Documento",
        },
    )

    fig.update_layout(coloraxis_showscale=False)

    return style_figure(
        fig,
        "15 documentos más lentos",
    )
