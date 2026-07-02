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
import threading
import tkinter as tk
from datetime import datetime
from tkinter import filedialog, messagebox, scrolledtext, ttk

import scraper_runner
import session_store

# Sitios del dropdown: etiqueta visible -> slug del scraper (None = no disponible).
SITIOS = {
    "OCC": "occ",
    "Computrabajo": "computrabajo",
    "Indeed (próximamente)": None,
}


class App(tk.Tk):
    def __init__(self):
        super().__init__()
        self.title("Buscador de Vacantes — OCC / Computrabajo")
        self.geometry("760x640")
        self.minsize(640, 520)

        self.sesion = session_store.cargar()
        self.cola_log = queue.Queue()      # líneas del scraper -> GUI
        self.proceso_ref = []              # referencia al Popen para cancelar
        self.buscando = False

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

        # --- Parámetros de búsqueda ---
        marco = ttk.LabelFrame(cont, text="Parámetros de búsqueda", padding=10)
        marco.pack(fill="x")

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

        ttk.Label(marco, text='Ej.: "ciudad de mexico", "guadalajara"',
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

    # ----------------------------------------------------------- Acciones --
    def _iniciar_busqueda(self):
        if self.buscando:
            return
        sitio = SITIOS.get(self.var_sitio.get())
        if sitio is None:
            messagebox.showinfo("Próximamente",
                                "Indeed aún no está disponible. Elige OCC o Computrabajo.")
            return
        empleo = self.entrada_empleo.get().strip()
        ciudad = self.entrada_ciudad.get().strip()
        if not empleo or not ciudad:
            messagebox.showwarning("Faltan datos", "Escribe la vacante (puesto) y la ciudad.")
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

    # --------------------------------------------------------- Auxiliares --
    def _set_botones(self, buscando: bool):
        estado_busqueda = "disabled" if buscando else "normal"
        self.btn_iniciar.config(state=estado_busqueda)
        hay_datos = bool(self.sesion["busquedas"])
        self.btn_siguiente.config(
            state="normal" if (hay_datos and not buscando) else "disabled")
        self.btn_finalizar.config(
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
            scraper_runner.cancelar(self.proceso_ref)
        self.destroy()


if __name__ == "__main__":
    App().mainloop()
