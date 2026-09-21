from __future__ import annotations

import pandas as pd
from dash import (
    Dash,
    Input,
    Output,
    callback,
    dcc,
    html,
)

from config import settings
from data.repository import VlmHubRepository
from ui.components import (
    NAV_ITEMS,
    filters,
    header,
)
from ui.pages import (
    documents as documents_page,
    errors as errors_page,
    executions as executions_page,
    models as models_page,
    overview as overview_page,
    servers as servers_page,
)


repo = VlmHubRepository(settings.db_path)

app = Dash(
    __name__,
    title="VlmHub · Processing Analytics",
    suppress_callback_exceptions=True,
)
server = app.server


def load_data():
    return (
        repo.load_documents(),
        repo.load_units(),
        repo.load_document_unit_summary(),
    )


def filter_data(
    documents,
    units,
    summary,
    models,
    servers,
    finish_reasons,
    start_date,
    end_date,
    search,
):
    docs = documents.copy()
    us = units.copy()
    sm = summary.copy()

    if start_date:
        start = pd.Timestamp(start_date)

        docs = docs[
            docs["FinishedAt"] >= start
        ]

        us = us[
            us["ProcessedAt"].isna()
            | (us["ProcessedAt"] >= start)
        ]

    if end_date:
        end = (
            pd.Timestamp(end_date)
            + pd.Timedelta(days=1)
        )

        docs = docs[
            docs["FinishedAt"] < end
        ]

        us = us[
            us["ProcessedAt"].isna()
            | (us["ProcessedAt"] < end)
        ]

    if models:
        us = us[
            us["Model"].isin(models)
        ]

    if servers:
        us = us[
            us["Server"].isin(servers)
        ]

    if finish_reasons:
        us = us[
            us["FinishReason"].isin(
                finish_reasons
            )
        ]

    # Si se filtran atributos de ProcessingUnit, solo permanecen documentos
    # que posean unidades compatibles con esos filtros.
    if models or servers or finish_reasons:
        allowed_docs = set(
            us["DocumentId"]
            .dropna()
            .tolist()
        )

        docs = docs[
            docs["Id"].isin(
                allowed_docs
            )
        ]

    if search:
        term = (
            str(search)
            .strip()
            .lower()
        )

        if term:
            searchable_columns = [
                "DocumentName",
                "OriginalKey",
                "ProcessingId",
                "ServerHost",
                "DatabaseName",
                "SourceTable",
                "Id",
            ]

            mask = pd.Series(
                False,
                index=docs.index,
            )

            for column in searchable_columns:
                mask = mask | (
                    docs[column]
                    .astype(str)
                    .str.lower()
                    .str.contains(
                        term,
                        na=False,
                        regex=False,
                    )
                )

            docs = docs[mask]

    doc_ids = set(
        docs["Id"].tolist()
    )

    if not us.empty:
        us = us[
            us["DocumentId"].isin(
                doc_ids
            )
        ]

    if not sm.empty:
        sm = sm[
            sm["DocumentId"].isin(
                doc_ids
            )
        ]

    return docs, us, sm


def schema_banner():
    problems = repo.validate_schema()

    if not problems:
        return html.Div()

    return html.Div(
        className="schema-error",
        children=[
            html.Strong(
                "Problema con el esquema SQLite: "
            ),
            html.Span(
                " | ".join(problems)
            ),
        ],
    )


app.layout = html.Div(
    className="app-shell",
    children=[
        dcc.Location(
            id="url",
            refresh=False,
        ),
        dcc.Interval(
            id="refresh",
            interval=(
                settings.refresh_seconds
                * 1000
            ),
            n_intervals=0,
        ),
        dcc.Store(
            id="last-refresh-store"
        ),
        header(),
        html.Main(
            className="page",
            children=[
                schema_banner(),
                filters(),
                html.Div(
                    id="page-content",
                ),
                html.Footer(
                    [
                        html.Span(
                            f"Base: {repo.db_path}"
                        ),
                        html.Span(" · "),
                        html.Span(
                            "Solo documentos con FinishedAt"
                        ),
                        html.Span(" · "),
                        html.Span(
                            f"Actualización: {settings.refresh_seconds}s"
                        ),
                    ]
                ),
            ],
        ),
    ],
)


@callback(
    Output("model-filter", "options"),
    Output("server-filter", "options"),
    Output("finish-reason-filter", "options"),
    Output("date-filter", "min_date_allowed"),
    Output("date-filter", "max_date_allowed"),
    Output("last-refresh-store", "data"),
    Input("refresh", "n_intervals"),
)
def refresh_filter_options(_):
    try:
        documents, units, _ = load_data()
    except Exception as exc:
        return (
            [],
            [],
            [],
            None,
            None,
            {
                "error": str(exc),
                "timestamp": pd.Timestamp.now().isoformat(),
            },
        )

    models = (
        sorted(
            units["Model"]
            .dropna()
            .unique()
            .tolist()
        )
        if not units.empty
        else []
    )

    servers = (
        sorted(
            units["Server"]
            .dropna()
            .unique()
            .tolist()
        )
        if not units.empty
        else []
    )

    finish_reasons = (
        sorted(
            units["FinishReason"]
            .dropna()
            .unique()
            .tolist()
        )
        if not units.empty
        else []
    )

    dates = (
        documents["FinishedAt"]
        .dropna()
        .tolist()
        if not documents.empty
        else []
    )

    min_date = (
        min(dates).date().isoformat()
        if dates
        else None
    )

    max_date = (
        max(dates).date().isoformat()
        if dates
        else None
    )

    return (
        [
            {"label": value, "value": value}
            for value in models
        ],
        [
            {"label": value, "value": value}
            for value in servers
        ],
        [
            {"label": value, "value": value}
            for value in finish_reasons
        ],
        min_date,
        max_date,
        {
            "timestamp": pd.Timestamp.now().isoformat(),
            "error": None,
        },
    )


@callback(
    Output("page-content", "children"),
    Input("url", "pathname"),
    Input("refresh", "n_intervals"),
    Input("model-filter", "value"),
    Input("server-filter", "value"),
    Input("finish-reason-filter", "value"),
    Input("date-filter", "start_date"),
    Input("date-filter", "end_date"),
    Input("search-filter", "value"),
)
def render_page(
    pathname,
    _,
    models,
    servers,
    finish_reasons,
    start_date,
    end_date,
    search,
):
    try:
        documents, units, summary = load_data()

        documents, units, summary = filter_data(
            documents,
            units,
            summary,
            models,
            servers,
            finish_reasons,
            start_date,
            end_date,
            search,
        )
    except Exception as exc:
        return html.Div(
            className="runtime-error",
            children=[
                html.H2("No fue posible leer SQLite"),
                html.Pre(str(exc)),
            ],
        )

    page_map = {
        "/": overview_page.render,
        "/ejecuciones": executions_page.render,
        "/modelos": models_page.render,
        "/servidores": servers_page.render,
        "/documentos": documents_page.render,
        "/errores": errors_page.render,
    }

    renderer = page_map.get(
        pathname,
        overview_page.render,
    )

    return renderer(
        documents,
        units,
        summary,
    )


@callback(
    Output("last-refresh", "children"),
    Input("last-refresh-store", "data"),
)
def show_refresh(data):
    if not data:
        return ""

    timestamp = (
        pd.Timestamp(
            data["timestamp"]
        )
        .strftime(
            "%d-%m-%Y %H:%M:%S"
        )
    )

    if data.get("error"):
        return f"Error SQLite · {timestamp}"

    return f"Actualizado · {timestamp}"


@callback(
    *[
        Output(nav_id, "className")
        for _, _, nav_id in NAV_ITEMS
    ],
    Input("url", "pathname"),
)
def highlight_nav(pathname):
    return tuple(
        (
            "nav-link nav-link-active"
            if href == pathname
            or (
                href == "/"
                and pathname not in {
                    item[0]
                    for item in NAV_ITEMS
                }
            )
            else "nav-link"
        )
        for href, _, _ in NAV_ITEMS
    )


if __name__ == "__main__":
    app.run(
        host=settings.host,
        port=settings.port,
        debug=settings.debug,
    )
