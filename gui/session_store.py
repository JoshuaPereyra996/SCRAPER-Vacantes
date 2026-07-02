#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Caché de sesión en disco para la GUI del scraper de vacantes.

Cada búsqueda ejecutada se anexa a un archivo JSON (session_cache.json) de modo
que, si la app se cierra o falla, la sesión se puede retomar al reabrir.

Estructura del caché:
{
  "creada": "2026-07-01T10:00:00",
  "busquedas": [
    {
      "sitio": "occ",
      "empleo": "analista",
      "ciudad": "ciudad-de-mexico",
      "timestamp": "2026-07-01T10:05:00",
      "vacantes": [ { ...Vacante... }, ... ]
    },
    ...
  ]
}
"""
import json
import os
from datetime import datetime

# El caché vive junto a este módulo (carpeta gui/).
RUTA_CACHE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "session_cache.json")


def _ahora() -> str:
    return datetime.now().isoformat(timespec="seconds")


def cargar() -> dict:
    """Carga el caché de sesión; devuelve una sesión vacía si no existe o está corrupto."""
    if not os.path.exists(RUTA_CACHE):
        return {"creada": _ahora(), "busquedas": []}
    try:
        with open(RUTA_CACHE, encoding="utf-8") as f:
            datos = json.load(f)
        if isinstance(datos, dict) and isinstance(datos.get("busquedas"), list):
            return datos
    except (json.JSONDecodeError, OSError):
        pass
    # Corrupto: se respalda y se empieza de cero (no se pierde el archivo original).
    try:
        os.replace(RUTA_CACHE, RUTA_CACHE + ".corrupto")
    except OSError:
        pass
    return {"creada": _ahora(), "busquedas": []}


def guardar(sesion: dict) -> None:
    """Escribe el caché de forma atómica (tmp + replace) para no corromperlo a medias."""
    tmp = RUTA_CACHE + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(sesion, f, ensure_ascii=False, indent=2)
    os.replace(tmp, RUTA_CACHE)


def agregar_busqueda(sesion: dict, sitio: str, empleo: str, ciudad: str,
                     vacantes: list) -> dict:
    """Anexa una búsqueda a la sesión y persiste el caché. Devuelve la sesión."""
    sesion["busquedas"].append({
        "sitio": sitio,
        "empleo": empleo,
        "ciudad": ciudad,
        "timestamp": _ahora(),
        "vacantes": vacantes,
    })
    guardar(sesion)
    return sesion


def limpiar() -> dict:
    """Borra el caché de disco y devuelve una sesión nueva vacía."""
    try:
        if os.path.exists(RUTA_CACHE):
            os.remove(RUTA_CACHE)
    except OSError:
        pass
    return {"creada": _ahora(), "busquedas": []}


def hay_sesion_pendiente() -> bool:
    """True si existe un caché con al menos una búsqueda acumulada."""
    if not os.path.exists(RUTA_CACHE):
        return False
    return len(cargar()["busquedas"]) > 0


def total_vacantes(sesion: dict) -> int:
    """Número total de vacantes acumuladas (con posibles duplicados entre búsquedas)."""
    return sum(len(b.get("vacantes", [])) for b in sesion["busquedas"])
