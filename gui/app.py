#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
GUI de escritorio del scraper de vacantes (Tkinter).

Flujo:
  1. Elegir bolsa de trabajo (OCC / Computrabajo / Indeed próximamente).
  2. Escribir Puesto y Ciudad → "Iniciar Búsqueda" ejecuta el scraper .NET en un
     hilo, muestra su log en vivo y acumula el JSON en el caché de sesión (disco).
  3. "Siguiente Búsqueda" limpia los campos conservando lo acumulado.
  4. "Finalizar y Exportar" genera un .docx con los JSON concatenados.

Ejecutar:  python3 gui/app.py   (desde la raíz del proyecto o desde gui/)
Requiere:  pip install python-docx   (solo para exportar)
"""
import queue
import re
import threading
import tkinter as tk
import urllib.parse
import webbrowser
from datetime import datetime
from tkinter import filedialog, messagebox, scrolledtext, ttk

import scraper_runner
import session_store

# Sitios del dropdown: etiqueta visible -> slug del scraper.
# Los que empiezan con "manual:" no se scrapean (sus términos prohíben bots):
# se abre el enlace de búsqueda en el navegador y las vacantes se capturan a mano.
SITIOS = {
    "OCC": "occ",
    "Computrabajo": "computrabajo",
    "Trabajos.mx": "trabajos",
    "Reclutalia": "reclutalia",
    "LinkedIn (semi-manual)": "manual:linkedin",
    "Indeed (semi-manual)": "manual:indeed",
}

# Plantillas de deep link de búsqueda para los sitios semi-manuales.
DEEP_LINKS = {
    "linkedin": "https://mx.linkedin.com/jobs/search?keywords={q}&location={l}",
    "indeed": "https://mx.indeed.com/jobs?q={q}&l={l}",
}


class App(tk.Tk):
    def __init__(self):
        super().__init__()
        self.title("Buscador de Vacantes — OCC / Computrabajo / Trabajos.mx / Reclutalia")
        self.geometry("760x640")
        self.minsize(640, 520)

        self.sesion = session_store.cargar()
        self.cola_log = queue.Queue()      # líneas del scraper -> GUI
        self.proceso_ref = []              # referencia al Popen para cancelar
        self.buscando = False
        self.lote_cancelado = False

        self._construir_ui()
        self._refrescar_historial()
        self.after(100, self._vaciar_cola_log)
        self.protocol("WM_DELETE_WINDOW", self._al_cerrar)

        # Si hay sesión pendiente de una ejecución anterior, ofrecer retomarla.
        if self.sesion["busquedas"]:
            retomar = messagebox.askyesno(
                "Sesión anterior",
                f"Hay una sesión con {len(self.sesion['busquedas'])} búsqueda(s) y "
                f"{session_store.total_vacantes(self.sesion)} vacante(s) acumuladas.\n\n"
                "¿Quieres retomarla? (No = empezar sesión nueva)")
            if not retomar:
                self.sesion = session_store.limpiar()
                self._refrescar_historial()

    # ------------------------------------------------------------------ UI --
    def _construir_ui(self):
        cont = ttk.Frame(self, padding=12)
        cont.pack(fill="both", expand=True)

        # --- Pestañas: búsqueda individual / modo lote ---
        pestanas = ttk.Notebook(cont)
        pestanas.pack(fill="x")

        marco = ttk.Frame(pestanas, padding=10)
        pestanas.add(marco, text="Búsqueda individual")

        marco_lote = ttk.Frame(pestanas, padding=10)
        pestanas.add(marco_lote, text="Modo lote (varios términos)")
        self._construir_tab_lote(marco_lote)

        ttk.Label(marco, text="Bolsa de trabajo:").grid(row=0, column=0, sticky="w")
        self.var_sitio = tk.StringVar(value="OCC")
        self.combo_sitio = ttk.Combobox(
            marco, textvariable=self.var_sitio, state="readonly",
            values=list(SITIOS.keys()), width=28)
        self.combo_sitio.grid(row=0, column=1, sticky="w", padx=(8, 24))

        ttk.Label(marco, text="Vacante (puesto):").grid(row=1, column=0, sticky="w", pady=(8, 0))
        self.entrada_empleo = ttk.Entry(marco, width=30)
        self.entrada_empleo.grid(row=1, column=1, sticky="w", padx=(8, 24), pady=(8, 0))

        ttk.Label(marco, text="Ciudad / Ubicación:").grid(row=2, column=0, sticky="w", pady=(8, 0))
        self.entrada_ciudad = ttk.Entry(marco, width=30)
        self.entrada_ciudad.grid(row=2, column=1, sticky="w", padx=(8, 24), pady=(8, 0))

        ttk.Label(marco, text='Ej.: "ciudad de mexico", "guadalajara" (Trabajos.mx filtra por estado)',
                  foreground="#777").grid(row=2, column=2, sticky="w")

        # --- Botones ---
        fila_botones = ttk.Frame(cont)
        fila_botones.pack(fill="x", pady=10)


        self.btn_iniciar = ttk.Button(fila_botones, text="▶  Iniciar Búsqueda",
                                      command=self._iniciar_busqueda)
        self.btn_iniciar.pack(side="left")

        self.btn_siguiente = ttk.Button(fila_botones, text="⏭  Siguiente Búsqueda",
                                        command=self._siguiente_busqueda, state="disabled")
        self.btn_siguiente.pack(side="left", padx=8)

        self.btn_finalizar = ttk.Button(fila_botones, text="💾  Finalizar y Exportar (.docx)",
                                        command=self._finalizar_exportar, state="disabled")
        self.btn_finalizar.pack(side="left")

        self.btn_excel = ttk.Button(fila_botones, text="📊  Excel puntuado (IA)",
                                    command=self._abrir_puntaje, state="disabled")
        self.btn_excel.pack(side="left", padx=8)

        self.lbl_estado = ttk.Label(fila_botones, text="Listo.", foreground="#333")
        self.lbl_estado.pack(side="right")

        # --- Historial de la sesión ---
        marco_hist = ttk.LabelFrame(cont, text="Búsquedas acumuladas en esta sesión", padding=8)
        marco_hist.pack(fill="x")
        self.lista_hist = tk.Listbox(marco_hist, height=5)
        self.lista_hist.pack(fill="x")
        self.lbl_total = ttk.Label(marco_hist, text="Total: 0 vacantes")
        self.lbl_total.pack(anchor="e", pady=(4, 0))

        # --- Log del scraper ---
        marco_log = ttk.LabelFrame(cont, text="Registro del scraper", padding=8)
        marco_log.pack(fill="both", expand=True, pady=(10, 0))
        self.log = scrolledtext.ScrolledText(marco_log, height=12, state="disabled",
                                             font=("Courier", 11))
        self.log.pack(fill="both", expand=True)

    def _construir_tab_lote(self, marco):
        """Pestaña de modo lote: N términos × portales seleccionados en cola."""
        ttk.Label(marco, text="Términos de búsqueda (uno por línea, pégalos de tu IA):"
                  ).grid(row=0, column=0, sticky="w")
        self.texto_terminos = tk.Text(marco, height=6, width=42)
        self.texto_terminos.grid(row=1, column=0, rowspan=4, sticky="w", padx=(0, 24))

        ttk.Label(marco, text="Ciudad / Ubicación:").grid(row=0, column=1, sticky="w")
        self.entrada_ciudad_lote = ttk.Entry(marco, width=26)
        self.entrada_ciudad_lote.grid(row=1, column=1, sticky="nw")

        # Portales scrapeables (los semi-manuales no aplican en lote).
        self.vars_portales = {}
        fila = 2
        for etiqueta, slug in SITIOS.items():
            if slug.startswith("manual:"):
                continue
            var = tk.BooleanVar(value=True)
            self.vars_portales[slug] = var
            ttk.Checkbutton(marco, text=etiqueta, variable=var).grid(
                row=fila, column=1, sticky="w")
            fila += 1

        self.btn_lote = ttk.Button(marco, text="🚀  Ejecutar lote",
                                   command=self._ejecutar_lote)
        self.btn_lote.grid(row=5, column=0, sticky="w", pady=(8, 0))
        self.btn_detener_lote = ttk.Button(marco, text="⛔  Detener lote",
                                           command=self._detener_lote, state="disabled")
        self.btn_detener_lote.grid(row=5, column=1, sticky="w", pady=(8, 0))

        self.lbl_progreso_lote = ttk.Label(marco, text="", foreground="#555")
        self.lbl_progreso_lote.grid(row=6, column=0, columnspan=2, sticky="w", pady=(6, 0))

    # ----------------------------------------------------------- Acciones --
    def _ejecutar_lote(self):
        if self.buscando:
            return
        terminos = [t.strip() for t in self.texto_terminos.get("1.0", "end").splitlines()
                    if t.strip()]
        ciudad = self.entrada_ciudad_lote.get().strip()
        portales = [s for s, v in self.vars_portales.items() if v.get()]

        if not terminos or not ciudad or not portales:
            messagebox.showwarning(
                "Faltan datos",
                "Necesito al menos un término, la ciudad y un portal seleccionado.")
            return

        cola = [(sitio, termino) for termino in terminos for sitio in portales]
        if not messagebox.askyesno(
                "Confirmar lote",
                f"Se ejecutarán {len(cola)} búsquedas "
                f"({len(terminos)} términos × {len(portales)} portales).\n"
                "Puedes dejar la app trabajando sola. ¿Continuar?"):
            return

        self.buscando = True
        self.lote_cancelado = False
        self._set_botones(buscando=True)
        self.btn_lote.config(state="disabled")
        self.btn_detener_lote.config(state="normal")

        hilo = threading.Thread(target=self._trabajo_lote, args=(cola, ciudad), daemon=True)
        hilo.start()

    def _trabajo_lote(self, cola, ciudad):
        """Corre en un hilo: ejecuta la cola completa, una búsqueda a la vez."""
        total = len(cola)
        exitosas = 0
        for i, (sitio, termino) in enumerate(cola, start=1):
            if self.lote_cancelado:
                self.cola_log.put(f"[lote] Detenido por el usuario en {i - 1}/{total}.")
                break

            self.after(0, self.lbl_progreso_lote.config,
                       {"text": f"Búsqueda {i}/{total} — {sitio}: '{termino}'…"})
            self.cola_log.put(f"===== [lote {i}/{total}] {sitio}: '{termino}' en '{ciudad}' =====")

            resultado = scraper_runner.ejecutar_busqueda(
                sitio, termino, ciudad,
                log=self.cola_log.put,
                proceso_ref=self.proceso_ref)

            if resultado.exito:
                exitosas += 1
                # Anexar a la sesión desde el hilo de la GUI.
                self.after(0, self._lote_agregar, sitio, termino, ciudad, resultado.vacantes)
                self.cola_log.put(f"[lote] ok: {resultado.mensaje}")
            else:
                self.cola_log.put(f"[lote] FALLÓ ({sitio}: '{termino}'): {resultado.mensaje} "
                                  "— continúo con la siguiente.")

            # Pausa entre búsquedas para no martillar los sitios.
            if i < total and not self.lote_cancelado:
                threading.Event().wait(5)

        self.after(0, self._lote_terminado, exitosas, total)

    def _lote_agregar(self, sitio, termino, ciudad, vacantes):
        self.sesion = session_store.agregar_busqueda(
            self.sesion, sitio,
            scraper_runner._normalizar_slug(termino),
            scraper_runner._normalizar_slug(ciudad),
            vacantes)
        self._refrescar_historial()

    def _lote_terminado(self, exitosas, total):
        self.buscando = False
        self.lote_cancelado = False
        self.btn_lote.config(state="normal")
        self.btn_detener_lote.config(state="disabled")
        self.lbl_progreso_lote.config(
            text=f"Lote terminado: {exitosas}/{total} búsquedas con resultados.")
        self.lbl_estado.config(text="Lote terminado.")
        self._set_botones(buscando=False)
        messagebox.showinfo(
            "Lote terminado",
            f"{exitosas} de {total} búsquedas obtuvieron vacantes.\n"
            f"Total acumulado: {session_store.total_vacantes(self.sesion)} vacantes.")

    def _detener_lote(self):
        self.lote_cancelado = True
        scraper_runner.cancelar(self.proceso_ref)
        self.lbl_progreso_lote.config(text="Deteniendo lote (termina la búsqueda en curso)…")

    def _iniciar_busqueda(self):
        if self.buscando:
            return
        sitio = SITIOS.get(self.var_sitio.get())
        empleo = self.entrada_empleo.get().strip()
        ciudad = self.entrada_ciudad.get().strip()
        if not empleo or not ciudad:
            messagebox.showwarning("Faltan datos", "Escribe la vacante (puesto) y la ciudad.")
            return

        # Sitios semi-manuales (LinkedIn/Indeed): deep link + captura manual.
        if sitio.startswith("manual:"):
            self._busqueda_semimanual(sitio.split(":", 1)[1], empleo, ciudad)
            return

        self.buscando = True
        self._set_botones(buscando=True)
        self.lbl_estado.config(text=f"Buscando en {self.var_sitio.get()}…")
        self._log_linea(f"===== {datetime.now().strftime('%H:%M:%S')} — "
                        f"{sitio}: '{empleo}' en '{ciudad}' =====")

        hilo = threading.Thread(
            target=self._trabajo_busqueda, args=(sitio, empleo, ciudad), daemon=True)
        hilo.start()

    def _trabajo_busqueda(self, sitio, empleo, ciudad):
        """Corre en un hilo: ejecuta el scraper y reporta el resultado a la GUI."""
        resultado = scraper_runner.ejecutar_busqueda(
            sitio, empleo, ciudad,
            log=self.cola_log.put,
            proceso_ref=self.proceso_ref)
        # Volver al hilo de la GUI para tocar widgets/estado.
        self.after(0, self._busqueda_terminada, sitio, empleo, ciudad, resultado)

    def _busqueda_terminada(self, sitio, empleo, ciudad, resultado):
        self.buscando = False
        if resultado.exito:
            self.sesion = session_store.agregar_busqueda(
                self.sesion, sitio,
                scraper_runner._normalizar_slug(empleo),
                scraper_runner._normalizar_slug(ciudad),
                resultado.vacantes)
            self._log_linea(f"[ok] {resultado.mensaje} (agregadas a la sesión)")
            self.lbl_estado.config(text=f"Última búsqueda: {resultado.mensaje}")
            self._refrescar_historial()
        else:
            self._log_linea(f"[error] {resultado.mensaje}")
            self.lbl_estado.config(text="La búsqueda falló (ver registro).")
            messagebox.showerror("Búsqueda fallida", resultado.mensaje)
        self._set_botones(buscando=False)

    def _busqueda_semimanual(self, sitio, empleo, ciudad):
        """
        LinkedIn/Indeed prohíben bots en sus términos: aquí NO se scrapea.
        Se abre el enlace de búsqueda en el navegador del usuario y se muestra
        una ventana para pegar manualmente las vacantes de interés.
        """
        url = DEEP_LINKS[sitio].format(
            q=urllib.parse.quote_plus(empleo),
            l=urllib.parse.quote_plus(ciudad))
        webbrowser.open(url)
        self._log_linea(f"[{sitio}] Búsqueda abierta en el navegador: {url}")
        self._log_linea(f"[{sitio}] Copia y pega las vacantes que te interesen "
                        "en la ventana de captura.")
        VentanaCapturaManual(self, sitio, empleo, ciudad, self._captura_terminada)

    def _captura_terminada(self, sitio, empleo, ciudad, vacantes):
        """Callback de la ventana de captura manual: agrega lo capturado a la sesión."""
        if not vacantes:
            self.lbl_estado.config(text="Captura cancelada (sin vacantes).")
            return
        self.sesion = session_store.agregar_busqueda(
            self.sesion, sitio,
            scraper_runner._normalizar_slug(empleo),
            scraper_runner._normalizar_slug(ciudad),
            vacantes)
        self._log_linea(f"[ok] {len(vacantes)} vacante(s) de {sitio} agregadas manualmente.")
        self.lbl_estado.config(text=f"{len(vacantes)} vacante(s) de {sitio} agregadas.")
        self._refrescar_historial()

    def _siguiente_busqueda(self):
        """Limpia los campos para capturar nuevos parámetros; lo acumulado se conserva."""
        self.entrada_empleo.delete(0, "end")
        self.entrada_ciudad.delete(0, "end")
        self.entrada_empleo.focus_set()
        self.lbl_estado.config(text="Listo para la siguiente búsqueda.")

    def _finalizar_exportar(self):
        if not self.sesion["busquedas"]:
            messagebox.showinfo("Nada que exportar", "Aún no hay búsquedas acumuladas.")
            return
        try:
            import docx_export
        except ImportError:
            messagebox.showerror(
                "Falta python-docx",
                "Para exportar a Word instala la dependencia:\n\n"
                "    pip install python-docx")
            return

        sugerido = f"vacantes_sesion_{datetime.now().strftime('%Y%m%d_%H%M')}.docx"
        ruta = filedialog.asksaveasfilename(
            title="Guardar documento de la sesión",
            defaultextension=".docx",
            initialfile=sugerido,
            filetypes=[("Documento de Word", "*.docx")])
        if not ruta:
            return

        try:
            unicas = docx_export.exportar(self.sesion, ruta)
        except Exception as ex:
            messagebox.showerror("Error al exportar", str(ex))
            return

        self._log_linea(f"[ok] Documento generado: {ruta} ({unicas} vacantes únicas)")
        limpiar = messagebox.askyesno(
            "Exportación completada",
            f"Documento generado con {unicas} vacantes únicas:\n{ruta}\n\n"
            "¿Limpiar la sesión para empezar de cero?")
        if limpiar:
            self.sesion = session_store.limpiar()
            self._refrescar_historial()
            self.lbl_estado.config(text="Sesión nueva.")

    def _abrir_puntaje(self):
        """Abre la ventana del ciclo: prompt → respuesta de la IA → Excel puntuado."""
        if not self.sesion["busquedas"]:
            messagebox.showinfo("Nada que puntuar", "Aún no hay búsquedas acumuladas.")
            return
        try:
            import excel_export  # noqa: F401 (verificar dependencia openpyxl)
            import openpyxl      # noqa: F401
        except ImportError:
            messagebox.showerror(
                "Falta openpyxl",
                "Para generar el Excel instala la dependencia:\n\n"
                "    gui/.venv/bin/pip install openpyxl")
            return
        VentanaPuntaje(self, self.sesion)

    # --------------------------------------------------------- Auxiliares --
    def _set_botones(self, buscando: bool):
        estado_busqueda = "disabled" if buscando else "normal"
        self.btn_iniciar.config(state=estado_busqueda)
        hay_datos = bool(self.sesion["busquedas"])
        self.btn_siguiente.config(
            state="normal" if (hay_datos and not buscando) else "disabled")
        self.btn_finalizar.config(
            state="normal" if (hay_datos and not buscando) else "disabled")
        self.btn_excel.config(
            state="normal" if (hay_datos and not buscando) else "disabled")

    def _refrescar_historial(self):
        self.lista_hist.delete(0, "end")
        for b in self.sesion["busquedas"]:
            self.lista_hist.insert(
                "end",
                f"[{b['sitio']}] {b['empleo']} — {b['ciudad']}  "
                f"({len(b['vacantes'])} vacantes, {b['timestamp']})")
        self.lbl_total.config(
            text=f"Total: {session_store.total_vacantes(self.sesion)} vacantes "
                 f"en {len(self.sesion['busquedas'])} búsqueda(s)")
        self._set_botones(buscando=self.buscando)

    def _log_linea(self, texto: str):
        self.cola_log.put(texto)

    def _vaciar_cola_log(self):
        """Pasa las líneas encoladas por el hilo del scraper al widget de log."""
        try:
            while True:
                linea = self.cola_log.get_nowait()
                self.log.configure(state="normal")
                self.log.insert("end", linea + "\n")
                self.log.see("end")
                self.log.configure(state="disabled")
        except queue.Empty:
            pass
        self.after(100, self._vaciar_cola_log)

    def _al_cerrar(self):
        if self.buscando:
            if not messagebox.askyesno(
                    "Búsqueda en curso",
                    "Hay una búsqueda ejecutándose. ¿Cerrar de todas formas?\n"
                    "(Lo ya acumulado queda guardado en el caché de sesión.)"):
                return
            self.lote_cancelado = True
            scraper_runner.cancelar(self.proceso_ref)
        self.destroy()


class VentanaCapturaManual(tk.Toplevel):
    """
    Ventana para capturar vacantes a mano (LinkedIn/Indeed): el usuario pega la
    liga y la descripción de cada vacante que le interese y la app las normaliza
    al mismo formato Vacante del scraper.
    """

    def __init__(self, padre, sitio, empleo, ciudad, al_terminar):
        super().__init__(padre)
        self.title(f"Captura manual — {sitio} ({empleo} / {ciudad})")
        self.geometry("620x560")
        self.sitio = sitio
        self.empleo = empleo
        self.ciudad = ciudad
        self.al_terminar = al_terminar
        self.vacantes = []

        cont = ttk.Frame(self, padding=12)
        cont.pack(fill="both", expand=True)

        ttk.Label(cont, text="Pega los datos de la vacante (solo la liga y el "
                             "título son obligatorios):", foreground="#555"
                  ).pack(anchor="w", pady=(0, 6))

        self.entradas = {}
        for campo, etiqueta in [("url", "Liga (URL) *"), ("titulo", "Título *"),
                                ("empresa", "Empresa"), ("ubicacion", "Ubicación"),
                                ("salario", "Salario")]:
            fila = ttk.Frame(cont)
            fila.pack(fill="x", pady=2)
            ttk.Label(fila, text=etiqueta, width=14).pack(side="left")
            e = ttk.Entry(fila)
            e.pack(side="left", fill="x", expand=True)
            self.entradas[campo] = e

        ttk.Label(cont, text="Descripción (pégala desde la página):").pack(anchor="w", pady=(8, 2))
        self.texto_desc = scrolledtext.ScrolledText(cont, height=10)
        self.texto_desc.pack(fill="both", expand=True)

        fila_btn = ttk.Frame(cont)
        fila_btn.pack(fill="x", pady=(10, 0))
        ttk.Button(fila_btn, text="➕ Agregar vacante",
                   command=self._agregar).pack(side="left")
        self.lbl_cuenta = ttk.Label(fila_btn, text="0 capturadas")
        self.lbl_cuenta.pack(side="left", padx=12)
        ttk.Button(fila_btn, text="✔ Terminar captura",
                   command=self._terminar).pack(side="right")

        self.protocol("WM_DELETE_WINDOW", self._terminar)
        self.entradas["url"].focus_set()

    def _agregar(self):
        url = self.entradas["url"].get().strip()
        titulo = self.entradas["titulo"].get().strip()
        if not url or not titulo:
            messagebox.showwarning("Faltan datos", "La liga y el título son obligatorios.",
                                   parent=self)
            return

        self.vacantes.append({
            "fuente": self.sitio,
            "jobid": self._extraer_jobid(url),
            "titulo": titulo,
            "empresa": self.entradas["empresa"].get().strip() or None,
            "ubicacion": self.entradas["ubicacion"].get().strip() or None,
            "salario": self.entradas["salario"].get().strip() or None,
            "fechaPublicacion": datetime.now().strftime("%Y-%m-%d"),
            "descripcion": self.texto_desc.get("1.0", "end").strip() or None,
            "urlPublica": url,
        })
        self.lbl_cuenta.config(text=f"{len(self.vacantes)} capturadas")
        # Limpiar para la siguiente vacante.
        for e in self.entradas.values():
            e.delete(0, "end")
        self.texto_desc.delete("1.0", "end")
        self.entradas["url"].focus_set()

    def _extraer_jobid(self, url):
        """Intenta sacar un id estable de la URL (LinkedIn: /jobs/view/{id}; Indeed: jk=)."""
        m = re.search(r"(?:jobs/view/|jk=|currentJobId=)(\w+)", url)
        if m:
            return m.group(1)
        return f"manual-{abs(hash(url)) % 10**10}"

    def _terminar(self):
        self.al_terminar(self.sitio, self.empleo, self.ciudad, self.vacantes)
        self.destroy()


class VentanaPuntaje(tk.Toplevel):
    """
    Ciclo de Excel puntuado con IA (chat manual):
      Paso 1: pega el CV mejorado → 'Generar y copiar prompt' (queda en el portapapeles).
      Paso 2: pega el prompt en tu IA; copia su respuesta CSV.
      Paso 3: pega la respuesta aquí → 'Generar Excel'.
    """

    def __init__(self, padre, sesion):
        super().__init__(padre)
        self.title("Excel puntuado con IA")
        self.geometry("680x620")
        self.sesion = sesion

        import excel_export
        self.excel_export = excel_export

        cont = ttk.Frame(self, padding=12)
        cont.pack(fill="both", expand=True)

        ttk.Label(cont, text="Paso 1 — Pega el CV mejorado del cliente:",
                  font=("", 12, "bold")).pack(anchor="w")
        self.texto_cv = scrolledtext.ScrolledText(cont, height=9)
        self.texto_cv.pack(fill="both", expand=True, pady=(4, 6))

        fila1 = ttk.Frame(cont)
        fila1.pack(fill="x", pady=(0, 10))
        ttk.Button(fila1, text="📋 Generar y copiar prompt",
                   command=self._copiar_prompt).pack(side="left")
        self.lbl_paso1 = ttk.Label(fila1, text="", foreground="#2a7a2a")
        self.lbl_paso1.pack(side="left", padx=10)

        ttk.Label(cont, text="Paso 2 — Pega aquí la respuesta CSV de tu IA "
                             "(jobid,puntaje,razon):",
                  font=("", 12, "bold")).pack(anchor="w")
        self.texto_csv = scrolledtext.ScrolledText(cont, height=9)
        self.texto_csv.pack(fill="both", expand=True, pady=(4, 6))

        fila2 = ttk.Frame(cont)
        fila2.pack(fill="x")
        ttk.Button(fila2, text="📊 Generar Excel",
                   command=self._generar_excel).pack(side="left")
        self.lbl_paso2 = ttk.Label(fila2, text="", foreground="#555")
        self.lbl_paso2.pack(side="left", padx=10)

    def _copiar_prompt(self):
        prompt = self.excel_export.generar_prompt(
            self.sesion, self.texto_cv.get("1.0", "end"))
        self.clipboard_clear()
        self.clipboard_append(prompt)
        n = len(self.excel_export.consolidar(self.sesion))
        self.lbl_paso1.config(
            text=f"Prompt copiado al portapapeles ({n} vacantes). Pégalo en tu IA.")

    def _generar_excel(self):
        puntajes = self.excel_export.parsear_respuesta(self.texto_csv.get("1.0", "end"))
        if not puntajes:
            messagebox.showwarning(
                "Respuesta vacía",
                "No pude leer ningún puntaje. Verifica que pegaste la respuesta "
                "CSV de la IA (jobid,puntaje,razon).", parent=self)
            return

        sugerido = f"vacantes_puntuadas_{datetime.now().strftime('%Y%m%d_%H%M')}.xlsx"
        ruta = filedialog.asksaveasfilename(
            title="Guardar Excel puntuado",
            defaultextension=".xlsx",
            initialfile=sugerido,
            filetypes=[("Libro de Excel", "*.xlsx")],
            parent=self)
        if not ruta:
            return

        try:
            total, con_puntaje = self.excel_export.exportar_excel(
                self.sesion, puntajes, ruta)
        except Exception as ex:
            messagebox.showerror("Error al generar el Excel", str(ex), parent=self)
            return

        self.lbl_paso2.config(
            text=f"Excel generado: {con_puntaje}/{total} vacantes con puntaje.")
        messagebox.showinfo(
            "Excel generado",
            f"{ruta}\n\n{con_puntaje} de {total} vacantes recibieron puntaje de la IA."
            + ("" if con_puntaje == total else
               "\nLas vacantes sin puntaje aparecen al final de la hoja."),
            parent=self)


if __name__ == "__main__":
    App().mainloop()
