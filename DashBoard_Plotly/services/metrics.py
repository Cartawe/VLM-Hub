from __future__ import annotations

import re
from dataclasses import dataclass

import pandas as pd


@dataclass
class DashboardMetrics:
    total_documents: int = 0
    successful_documents: int = 0
    failed_documents: int = 0
    success_rate: float = 0.0
    avg_document_time: float = 0.0
    median_document_time: float = 0.0
    p95_document_time: float = 0.0
    attempts: int = 0
    retries: int = 0
    retry_rate: float = 0.0


@dataclass
class ParallelMetrics:
    parallel_seconds: float = 0.0
    serial_seconds: float = 0.0
    saved_seconds: float = 0.0
    saved_percent: float = 0.0
    speedup: float = 0.0
    executions: int = 0
    servers: int = 0


def _safe_float(value) -> float:
    if value is None or pd.isna(value):
        return 0.0
    return float(value)


def percentile(series: pd.Series, q: float) -> float:
    clean = pd.to_numeric(series, errors="coerce").dropna()
    if clean.empty:
        return 0.0
    return float(clean.quantile(q))


def calculate_metrics(
    documents: pd.DataFrame,
    units: pd.DataFrame,
) -> DashboardMetrics:
    if documents.empty:
        return DashboardMetrics()

    total = len(documents)
    successful = int(documents["Success"].sum())
    failed = total - successful

    times = pd.to_numeric(
        documents["ProcessingTime"], errors="coerce"
    ).dropna()

    retries = 0
    attempts = len(units)

    if not units.empty:
        attempts_per_unit = (
            units.groupby(
                ["DocumentId", "UnitIndex"],
                dropna=False,
            )
            .size()
            .rename("count")
        )
        retries = int(
            (attempts_per_unit - 1)
            .clip(lower=0)
            .sum()
        )

    return DashboardMetrics(
        total_documents=total,
        successful_documents=successful,
        failed_documents=failed,
        success_rate=(successful / total * 100) if total else 0.0,
        avg_document_time=_safe_float(times.mean()),
        median_document_time=_safe_float(times.median()),
        p95_document_time=percentile(times, 0.95),
        attempts=attempts,
        retries=retries,
        retry_rate=(retries / attempts * 100) if attempts else 0.0,
    )


def execution_parallel_summary(
    documents: pd.DataFrame,
    units: pd.DataFrame,
) -> pd.DataFrame:
    """
    Compara tiempo VLM paralelo observado contra un escenario serial teórico.

    SerialSeconds:
        suma de ProcessingUnit.ProcessingTime.
        Representa procesar todos los intentos uno detrás de otro en un solo
        servidor, exactamente según la regla solicitada: suma de tiempos.

    ParallelSeconds:
        para cada ProcessingId se infiere el inicio de cada intento como
        ProcessedAt - ProcessingTime y se mide desde el primer inicio hasta
        el último ProcessedAt.

    Esto utiliza exclusivamente tiempos VLM registrados por ProcessingUnit.
    """
    columns = [
        "ProcessingId",
        "ParallelSeconds",
        "SerialSeconds",
        "SavedSeconds",
        "SavedPercent",
        "Speedup",
        "Servers",
        "Documents",
        "Attempts",
        "StartedAt",
        "FinishedAt",
    ]

    if documents.empty or units.empty:
        return pd.DataFrame(columns=columns)

    doc_map = documents[
        ["Id", "ProcessingId"]
    ].rename(columns={"Id": "DocumentId"})

    df = units.merge(
        doc_map,
        on="DocumentId",
        how="inner",
    )

    df = df.dropna(
        subset=["ProcessedAt", "ProcessingTime", "ProcessingId"]
    ).copy()

    df = df[df["ProcessingTime"] >= 0].copy()

    if df.empty:
        return pd.DataFrame(columns=columns)

    df["AttemptStartedAt"] = (
        df["ProcessedAt"]
        - pd.to_timedelta(
            df["ProcessingTime"],
            unit="s",
        )
    )

    rows = []

    for processing_id, group in df.groupby(
        "ProcessingId",
        dropna=False,
    ):
        started_at = group["AttemptStartedAt"].min()
        finished_at = group["ProcessedAt"].max()

        parallel_seconds = max(
            0.0,
            (finished_at - started_at).total_seconds(),
        )

        serial_seconds = _safe_float(
            group["ProcessingTime"].sum()
        )

        saved_seconds = serial_seconds - parallel_seconds
        saved_percent = (
            saved_seconds / serial_seconds * 100
            if serial_seconds > 0
            else 0.0
        )
        speedup = (
            serial_seconds / parallel_seconds
            if parallel_seconds > 0
            else 0.0
        )

        rows.append(
            {
                "ProcessingId": str(processing_id),
                "ParallelSeconds": parallel_seconds,
                "SerialSeconds": serial_seconds,
                "SavedSeconds": saved_seconds,
                "SavedPercent": saved_percent,
                "Speedup": speedup,
                "Servers": int(group["Server"].nunique()),
                "Documents": int(group["DocumentId"].nunique()),
                "Attempts": int(len(group)),
                "StartedAt": started_at,
                "FinishedAt": finished_at,
            }
        )

    result = pd.DataFrame(rows)

    if result.empty:
        return pd.DataFrame(columns=columns)

    return result.sort_values(
        "FinishedAt",
        ascending=False,
    )


def calculate_parallel_metrics(
    documents: pd.DataFrame,
    units: pd.DataFrame,
) -> ParallelMetrics:
    executions = execution_parallel_summary(
        documents,
        units,
    )

    if executions.empty:
        return ParallelMetrics()

    parallel_seconds = _safe_float(
        executions["ParallelSeconds"].sum()
    )
    serial_seconds = _safe_float(
        executions["SerialSeconds"].sum()
    )
    saved_seconds = serial_seconds - parallel_seconds
    saved_percent = (
        saved_seconds / serial_seconds * 100
        if serial_seconds > 0
        else 0.0
    )
    speedup = (
        serial_seconds / parallel_seconds
        if parallel_seconds > 0
        else 0.0
    )

    server_count = (
        int(units["Server"].nunique())
        if not units.empty
        else 0
    )

    return ParallelMetrics(
        parallel_seconds=parallel_seconds,
        serial_seconds=serial_seconds,
        saved_seconds=saved_seconds,
        saved_percent=saved_percent,
        speedup=speedup,
        executions=len(executions),
        servers=server_count,
    )


def normalize_error(error: str) -> str:
    text = str(error or "").strip()
    if not text:
        return "Sin detalle"

    text = re.sub(
        r"https?://\S+",
        "<url>",
        text,
        flags=re.IGNORECASE,
    )
    text = re.sub(r"(/[^\s:]+)+", "<path>", text)
    text = re.sub(r"\b\d+\b", "<n>", text)
    text = re.sub(r"\s+", " ", text)

    return text[:160]


def error_summary(
    units: pd.DataFrame,
    top_n: int = 15,
) -> pd.DataFrame:
    if units.empty:
        return pd.DataFrame(columns=["Error", "Count"])

    df = units.loc[
        (~units["Success"])
        & units["Error"].astype(bool),
        ["Error"],
    ].copy()

    if df.empty:
        return pd.DataFrame(columns=["Error", "Count"])

    df["Error"] = df["Error"].map(normalize_error)

    return (
        df.groupby("Error")
        .size()
        .reset_index(name="Count")
        .sort_values("Count", ascending=False)
        .head(top_n)
    )


def failed_attempts_detail(
    documents: pd.DataFrame,
    units: pd.DataFrame,
) -> pd.DataFrame:
    if documents.empty or units.empty:
        return pd.DataFrame()

    docs = documents[
        ["Id", "ProcessingId", "DocumentName", "OriginalKey"]
    ].rename(columns={"Id": "DocumentId"})

    df = units.loc[
        ~units["Success"],
        [
            "Id",
            "DocumentId",
            "UnitIndex",
            "AttemptNumber",
            "Model",
            "Server",
            "FinishReason",
            "ProcessingTime",
            "ProcessedAt",
            "Error",
        ],
    ].merge(
        docs,
        on="DocumentId",
        how="left",
    )

    return df.sort_values(
        "ProcessedAt",
        ascending=False,
    )


def model_summary(units: pd.DataFrame) -> pd.DataFrame:
    return _group_summary(units, "Model")


def server_summary(units: pd.DataFrame) -> pd.DataFrame:
    return _group_summary(units, "Server")


def _group_summary(
    units: pd.DataFrame,
    group_column: str,
) -> pd.DataFrame:
    if units.empty:
        return pd.DataFrame(
            columns=[
                group_column,
                "Attempts",
                "Successes",
                "Failures",
                "SuccessRate",
                "AvgTime",
                "MedianTime",
                "P95Time",
            ]
        )

    grouped = units.groupby(
        group_column,
        dropna=False,
    )

    result = grouped.agg(
        Attempts=("Id", "count"),
        Successes=("Success", "sum"),
        AvgTime=("ProcessingTime", "mean"),
        MedianTime=("ProcessingTime", "median"),
    ).reset_index()

    result["Failures"] = (
        result["Attempts"] - result["Successes"]
    )
    result["SuccessRate"] = (
        result["Successes"]
        / result["Attempts"]
        * 100
    )

    p95 = (
        grouped["ProcessingTime"]
        .quantile(0.95)
        .rename("P95Time")
        .reset_index()
    )

    result = result.merge(
        p95,
        on=group_column,
        how="left",
    )

    return result.sort_values(
        ["SuccessRate", "Attempts"],
        ascending=[False, False],
    )


def processing_timeline(
    documents: pd.DataFrame,
) -> pd.DataFrame:
    if documents.empty:
        return pd.DataFrame(
            columns=[
                "Period",
                "Documents",
                "Successes",
                "Failures",
            ]
        )

    df = documents.dropna(
        subset=["FinishedAt"]
    ).copy()

    if df.empty:
        return pd.DataFrame(
            columns=[
                "Period",
                "Documents",
                "Successes",
                "Failures",
            ]
        )

    df["Period"] = (
        df["FinishedAt"].dt.floor("h")
    )

    grouped = (
        df.groupby("Period")
        .agg(
            Documents=("Id", "count"),
            Successes=("Success", "sum"),
        )
        .reset_index()
    )

    grouped["Failures"] = (
        grouped["Documents"]
        - grouped["Successes"]
    )

    return grouped


def finish_reason_summary(
    units: pd.DataFrame,
) -> pd.DataFrame:
    if units.empty:
        return pd.DataFrame(
            columns=["FinishReason", "Count"]
        )

    return (
        units["FinishReason"]
        .fillna("Sin finish reason")
        .value_counts()
        .rename_axis("FinishReason")
        .reset_index(name="Count")
    )


def format_seconds(value: float) -> str:
    value = _safe_float(value)

    sign = "-" if value < 0 else ""
    value = abs(value)

    if value < 60:
        return f"{sign}{value:.1f} s"

    minutes, seconds = divmod(value, 60)

    if value < 3600:
        return f"{sign}{int(minutes)}m {seconds:04.1f}s"

    hours, minutes = divmod(minutes, 60)

    if hours < 24:
        return f"{sign}{int(hours)}h {int(minutes)}m"

    days, hours = divmod(hours, 24)
    return f"{sign}{int(days)}d {int(hours)}h"
