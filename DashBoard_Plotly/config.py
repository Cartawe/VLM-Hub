from __future__ import annotations

import os
from dataclasses import dataclass


@dataclass(frozen=True)
class Settings:
    db_path: str = os.getenv("VLMHUB_DB", "./vlmhub.db")
    host: str = os.getenv("VLMHUB_DASHBOARD_HOST", "127.0.0.1")
    port: int = int(os.getenv("VLMHUB_DASHBOARD_PORT", "8050"))
    debug: bool = os.getenv("VLMHUB_DASHBOARD_DEBUG", "0") == "1"
    refresh_seconds: int = int(os.getenv("VLMHUB_DASHBOARD_REFRESH", "30"))


settings = Settings()
