from __future__ import annotations

import sqlite3
from contextlib import contextmanager
from pathlib import Path

import pandas as pd


DOCUMENT_REQUIRED = {
    "Id",
    "ProcessingId",
    "ServerHost",
    "DatabaseName",
    "SourceTable",
    "OriginalKey",
    "DocumentName",
    "ServerProcessingId",
    "Success",
    "ProcessingTime",
    "StartedAt",
    "FinishedAt",
}

PROCESSING_UNIT_REQUIRED = {
    "Id",
    "DocumentId",
    "UnitIndex",
    "AttemptNumber",
    "Model",
    "Server",
    "Success",
    "FinishReason",
    "ProcessingTime",
    "ProcessedAt",
    "Transcription",
    "Error",
}


class VlmHubRepository:
    def __init__(self, db_path: str):
        self.db_path = str(Path(db_path).expanduser().resolve())

    @contextmanager
    def connect(self):
        conn = sqlite3.connect(self.db_path, timeout=10)
        try:
            conn.execute("PRAGMA busy_timeout = 10000;")
            yield conn
        finally:
            conn.close()

    def _table_exists(self, conn: sqlite3.Connection, table: str) -> bool:
        row = conn.execute(
            """
            SELECT 1
            FROM sqlite_master
            WHERE type = 'table' AND name = ?
            LIMIT 1
            """,
            (table,),
        ).fetchone()
        return row is not None

    def _columns(self, conn: sqlite3.Connection, table: str) -> set[str]:
        if not self._table_exists(conn, table):
            return set()
        return {row[1] for row in conn.execute(f'PRAGMA table_info("{table}")')}

    def validate_schema(self) -> list[str]:
        problems: list[str] = []

        with self.connect() as conn:
            document_columns = self._columns(conn, "Document")
            unit_columns = self._columns(conn, "ProcessingUnit")

            if not document_columns:
                problems.append("No existe la tabla Document.")
            else:
                missing = DOCUMENT_REQUIRED - document_columns
                if missing:
                    problems.append(
                        "Document no contiene: " + ", ".join(sorted(missing))
                    )

            if not unit_columns:
                problems.append("No existe la tabla ProcessingUnit.")
            else:
                missing = PROCESSING_UNIT_REQUIRED - unit_columns
                if missing:
                    problems.append(
                        "ProcessingUnit no contiene: " + ", ".join(sorted(missing))
                    )

        return problems

    def load_documents(self) -> pd.DataFrame:
        """
        El dashboard analiza exclusivamente documentos procesados/finalizados.

        FinishedAt IS NOT NULL evita que documentos todavía activos contaminen
        KPIs de éxito, tiempos y comparaciones.
        """
        with self.connect() as conn:
            df = pd.read_sql_query(
                """
                SELECT
                    Id,
                    ProcessingId,
                    ServerHost,
                    DatabaseName,
                    SourceTable,
                    OriginalKey,
                    DocumentName,
                    ServerProcessingId,
                    Success,
                    ProcessingTime,
                    StartedAt,
                    FinishedAt
                FROM Document
                WHERE FinishedAt IS NOT NULL
                """,
                conn,
            )

        if df.empty:
            return df

        df["Success"] = df["Success"].fillna(0).astype(bool)
        df["ProcessingTime"] = pd.to_numeric(
            df["ProcessingTime"], errors="coerce"
        )
        df["StartedAt"] = pd.to_datetime(
            df["StartedAt"], errors="coerce"
        )
        df["FinishedAt"] = pd.to_datetime(
            df["FinishedAt"], errors="coerce"
        )

        for col in (
            "ProcessingId",
            "ServerHost",
            "DatabaseName",
            "SourceTable",
            "OriginalKey",
            "DocumentName",
            "ServerProcessingId",
        ):
            df[col] = df[col].fillna("").astype(str)

        df["Status"] = df["Success"].map(
            {True: "Éxito", False: "Error"}
        )

        return df

    def load_units(self) -> pd.DataFrame:
        """
        Solo se recuperan intentos pertenecientes a documentos finalizados.
        """
        with self.connect() as conn:
            df = pd.read_sql_query(
                """
                SELECT
                    p.Id,
                    p.DocumentId,
                    p.UnitIndex,
                    p.AttemptNumber,
                    p.Model,
                    p.Server,
                    p.Success,
                    p.FinishReason,
                    p.ProcessingTime,
                    p.ProcessedAt,
                    p.Error
                FROM ProcessingUnit p
                INNER JOIN Document d
                    ON d.Id = p.DocumentId
                WHERE d.FinishedAt IS NOT NULL
                """,
                conn,
            )

        if df.empty:
            return df

        df["Success"] = df["Success"].fillna(0).astype(bool)
        df["ProcessingTime"] = pd.to_numeric(
            df["ProcessingTime"], errors="coerce"
        )
        df["ProcessedAt"] = pd.to_datetime(
            df["ProcessedAt"], errors="coerce"
        )

        df["Model"] = df["Model"].fillna("Sin modelo").astype(str)
        df["Server"] = df["Server"].fillna("Sin servidor").astype(str)
        df["FinishReason"] = (
            df["FinishReason"].fillna("Sin finish reason").astype(str)
        )
        df["Error"] = df["Error"].fillna("").astype(str)

        return df

    def load_document_unit_summary(self) -> pd.DataFrame:
        with self.connect() as conn:
            df = pd.read_sql_query(
                """
                SELECT
                    d.Id AS DocumentId,
                    d.ProcessingId,
                    d.ServerHost,
                    d.DatabaseName,
                    d.SourceTable,
                    d.OriginalKey,
                    d.DocumentName,
                    d.ServerProcessingId,
                    d.Success AS DocumentSuccess,
                    d.ProcessingTime AS DocumentProcessingTime,
                    d.StartedAt,
                    d.FinishedAt,
                    COUNT(p.Id) AS Attempts,
                    COUNT(DISTINCT p.UnitIndex) AS Units,
                    SUM(CASE WHEN p.Success = 1 THEN 1 ELSE 0 END)
                        AS SuccessfulAttempts,
                    SUM(CASE WHEN p.Success = 0 THEN 1 ELSE 0 END)
                        AS FailedAttempts
                FROM Document d
                LEFT JOIN ProcessingUnit p
                    ON p.DocumentId = d.Id
                WHERE d.FinishedAt IS NOT NULL
                GROUP BY
                    d.Id,
                    d.ProcessingId,
                    d.ServerHost,
                    d.DatabaseName,
                    d.SourceTable,
                    d.OriginalKey,
                    d.DocumentName,
                    d.ServerProcessingId,
                    d.Success,
                    d.ProcessingTime,
                    d.StartedAt,
                    d.FinishedAt
                """,
                conn,
            )

        if df.empty:
            return df

        df["DocumentSuccess"] = (
            df["DocumentSuccess"].fillna(0).astype(bool)
        )
        df["DocumentProcessingTime"] = pd.to_numeric(
            df["DocumentProcessingTime"], errors="coerce"
        )
        df["StartedAt"] = pd.to_datetime(
            df["StartedAt"], errors="coerce"
        )
        df["FinishedAt"] = pd.to_datetime(
            df["FinishedAt"], errors="coerce"
        )
        df["Status"] = df["DocumentSuccess"].map(
            {True: "Éxito", False: "Error"}
        )

        return df
