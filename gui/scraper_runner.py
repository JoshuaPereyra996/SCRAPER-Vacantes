#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Ejecuta el scraper .NET existente (OccScraper) como subproceso y recoge su salida.

No re-implementa nada de scraping: reutiliza el proyecto C#/Playwright probado.
Flujo:
  1. Lanza `dotnet run -- --sitio X --empleo Y --ciudad Z` con cwd = raíz del proyecto.
  2. Transmite cada línea de stdout/stderr a un callback (para el log en vivo de la GUI).
  3. Al terminar, localiza el `vacantes_*.json` generado en bin/Debug/net8.0/output/
     (por fecha de modificación posterior al inicio) y lo carga.
"""
import glob
import json
import os
import subprocess
import time
from typing import Optional

# Raíz del proyecto C# = carpeta padre de gui/.
RAIZ_PROYECTO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CARPETA_SALIDA = os.path.join(RAIZ_PROYECTO, "bin", "Debug", "net8.0", "output")

# Mapeo de los códigos de salida definidos en Program.cs.
MENSAJES_CODIGO = {
    0: "OK",
    1: "Parámetros inválidos (empleo/ciudad/sitio).",
    2: "Error inesperado ejecutando el scraper (¿cambió el sitio? ¿falta Chromium de Playwright?).",
    3: "No se obtuvieron vacantes (prueba otros términos o revisa los selectores).",
    4: "Error guardando el contenido crudo.",
    5: "Error generando el JSON limpio.",
}


class ResultadoBusqueda:
    """Resultado de una ejecución del scraper."""

    def __init__(self, exito: bool, mensaje: str, vacantes: list, archivo: Optional[str]):
        self.exito = exito
        self.mensaje = mensaje
        self.vacantes = vacantes
        self.archivo = archivo


def _normalizar_slug(texto: str) -> str:
    """Misma normalización que Program.cs: minúsculas y espacios -> guiones."""
    return texto.strip().lower().replace(" ", "-")


def ejecutar_busqueda(sitio: str, empleo: str, ciudad: str,
                      log=print, proceso_ref=None) -> ResultadoBusqueda:
    """
    Ejecuta UNA búsqueda con el scraper .NET.

    log:          callback que recibe cada línea de salida del proceso (log en vivo).
    proceso_ref:  lista opcional; se le anexa el Popen para poder cancelarlo desde fuera.
    """
    empleo_slug = _normalizar_slug(empleo)
    ciudad_slug = _normalizar_slug(ciudad)
    inicio = time.time()

    comando = [
        "dotnet", "run", "--",
        "--sitio", sitio,
        "--empleo", empleo_slug,
        "--ciudad", ciudad_slug,
    ]
    log(f"$ {' '.join(comando)}")

    try:
        proceso = subprocess.Popen(
            comando,
            cwd=RAIZ_PROYECTO,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,   # mezclar stderr en el mismo flujo, en orden
            text=True,
            encoding="utf-8",
            errors="replace",
        )
    except FileNotFoundError:
        return ResultadoBusqueda(
            False,
            "No se encontró el comando 'dotnet'. Instala el SDK de .NET y verifica el PATH.",
            [], None)

    if proceso_ref is not None:
        proceso_ref.append(proceso)

    # Transmitir la salida línea a línea al log de la GUI.
    assert proceso.stdout is not None
    for linea in proceso.stdout:
        log(linea.rstrip())

    codigo = proceso.wait()
    if codigo != 0:
        mensaje = MENSAJES_CODIGO.get(codigo, f"El scraper terminó con código {codigo}.")
        return ResultadoBusqueda(False, mensaje, [], None)

    # Localizar el vacantes_*.json generado por ESTA ejecución.
    archivo = _buscar_json_reciente(inicio)
    if archivo is None:
        return ResultadoBusqueda(
            False,
            "El scraper terminó bien pero no se encontró el JSON de salida en "
            f"{CARPETA_SALIDA}.",
            [], None)

    try:
        with open(archivo, encoding="utf-8") as f:
            vacantes = json.load(f)
    except (json.JSONDecodeError, OSError) as ex:
        return ResultadoBusqueda(False, f"No se pudo leer {archivo}: {ex}", [], archivo)

    if not isinstance(vacantes, list):
        return ResultadoBusqueda(False, f"Formato inesperado en {archivo}.", [], archivo)

    return ResultadoBusqueda(
        True,
        f"{len(vacantes)} vacantes obtenidas.",
        vacantes,
        archivo)


def _buscar_json_reciente(desde_timestamp: float) -> Optional[str]:
    """Devuelve el vacantes_*.json más reciente modificado después de `desde_timestamp`."""
    patron = os.path.join(CARPETA_SALIDA, "vacantes_*.json")
    candidatos = [
        (os.path.getmtime(p), p)
        for p in glob.glob(patron)
        if os.path.getmtime(p) >= desde_timestamp - 1  # margen de 1 s por redondeos
    ]
    if not candidatos:
        return None
    return max(candidatos)[1]


def cancelar(proceso_ref) -> None:
    """Termina el proceso del scraper si sigue vivo (para el cierre de la GUI)."""
    for p in proceso_ref:
        if p.poll() is None:
            try:
                p.terminate()
            except OSError:
                pass
