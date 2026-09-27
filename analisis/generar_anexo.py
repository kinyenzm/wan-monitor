#!/usr/bin/env python3
"""Genera el anexo de cortes y el resumen de cifras a partir de los registros del monitor.

Entradas (carpeta de registros del monitor, por defecto %LOCALAPPDATA%\\WanMonitor):
  cortes.csv              una fila por corte, escrita por el monitor
  sondas-AAAA-MM-DD.log   cada sonda fallida con marca de tiempo al milisegundo y un latido por minuto

Reglas (configurables en eventos.json):
  - Ruido: eventos de menos de `umbral_ruido_s` segundos (saturación momentánea, no pérdida de servicio).
  - Excluidos: eventos provocados por las pruebas (cambios de modo de red, reinicios del equipo y sus arranques).
  - Inválidos: registros sin validez (por ejemplo, el computador de medición suspendido).
  - Origen: LAN si en la mitad o más de las sondas fallidas tampoco respondió el router; si no, WAN.
  - Condición: etiqueta del último tramo de `condiciones` cuyo inicio es anterior al corte.

Uso:
  python generar_anexo.py --datos <carpeta de registros> --eventos eventos.json --salida <carpeta>
"""
import argparse, csv, glob, json, os, re, statistics as st
from datetime import datetime, timedelta

FMT = '%Y-%m-%d %H:%M:%S'
LINEA = re.compile(r'(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d)\.\d+ (.*)')


def leer_sondas(carpeta, hasta):
    lineas = []
    for fn in sorted(glob.glob(os.path.join(carpeta, 'sondas-*.log'))):
        with open(fn, encoding='utf-8-sig', errors='replace') as f:
            for l in f:
                m = LINEA.match(l.strip())
                if m:
                    t = datetime.strptime(m.group(1), FMT)
                    if hasta is None or t < hasta:
                        lineas.append((t, m.group(2)))
    lineas.sort()
    return lineas


def horas_medidas(lineas, hueco_max_s=300):
    """Tiempo cubierto por el monitor: suma de intervalos entre registros consecutivos de hasta 5 min."""
    return sum((b - a).total_seconds() for (a, _), (b, _) in zip(lineas, lineas[1:])
               if (b - a).total_seconds() <= hueco_max_s) / 3600


def fallos_gateway_desde_log(lineas, ini, fin):
    """Para filas antiguas del CSV sin la columna fallos_gateway: se cuenta en el log de sondas."""
    return sum(1 for t, x in lineas
               if ini <= t < fin + timedelta(seconds=1) and x.startswith('FALLO') and 'gateway=sin respuesta' in x)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('--datos', default=os.path.join(os.environ.get('LOCALAPPDATA', '.'), 'WanMonitor'))
    ap.add_argument('--eventos', default=os.path.join(os.path.dirname(__file__), 'eventos.json'))
    ap.add_argument('--salida', default='.')
    a = ap.parse_args()

    ev = json.load(open(a.eventos, encoding='utf-8'))
    hasta = datetime.strptime(ev['hasta'], FMT) if ev.get('hasta') else None
    umbral = ev.get('umbral_ruido_s', 5)
    excluir = {e['inicio'] for e in ev.get('excluir', [])}
    invalidos = {e['inicio'] for e in ev.get('invalidos', [])}
    tramos = sorted((datetime.strptime(c['desde'], FMT), c['etiqueta']) for c in ev.get('condiciones', []))

    lineas = leer_sondas(a.datos, hasta)
    filas, ruido, n_excl, n_inv = [], 0, 0, 0
    with open(os.path.join(a.datos, 'cortes.csv'), encoding='utf-8-sig') as f:
        for r in list(csv.reader(f, delimiter=';'))[1:]:
            ini, fin = datetime.strptime(f'{r[0]} {r[1]}', FMT), datetime.strptime(f'{r[0]} {r[2]}', FMT)
            if hasta and ini >= hasta:
                continue
            clave = f'{r[0]} {r[1]}'
            if clave in excluir: n_excl += 1; continue
            if clave in invalidos: n_inv += 1; continue
            if int(r[3]) < umbral: ruido += 1; continue
            fallos = int(r[5])
            gw = int(r[6]) if len(r) >= 7 and r[6] != '' else fallos_gateway_desde_log(lineas, ini, fin)
            origen = 'LAN (red local)' if gw * 2 >= fallos else 'WAN (enlace del operador)'
            etiqueta = ''
            for desde, et in tramos:
                if desde <= ini: etiqueta = et
            filas.append([r[0], r[1], r[2], r[3], str(fallos), str(gw), origen, etiqueta])
    filas.sort(key=lambda x: (x[0], x[1]))

    os.makedirs(a.salida, exist_ok=True)
    with open(os.path.join(a.salida, 'anexo-cortes.csv'), 'w', encoding='utf-8-sig', newline='') as f:
        w = csv.writer(f, delimiter=';', lineterminator='\r\n')
        w.writerow(['fecha', 'hora_inicio', 'hora_fin', 'duracion_s', 'sondas_sin_internet',
                    'sondas_sin_respuesta_del_router', 'origen', 'condicion_del_equipo'])
        w.writerows(filas)

    inicio = [datetime.strptime(f'{x[0]} {x[1]}', FMT) for x in filas]
    dur = [int(x[3]) for x in filas]
    per = [(b - a).total_seconds() for a, b in zip(inicio, inicio[1:])]
    reg = [p for p in per if 620 <= p <= 650]
    horas = horas_medidas(lineas)
    tot = sum(dur)
    fallos = sum(int(x[4]) for x in filas); gw = sum(int(x[5]) for x in filas)
    min_dia = tot / (horas * 3600) * 86400 / 60
    resumen = {
        'cortes': len(filas), 'desde': filas[0][0] + ' ' + filas[0][1], 'hasta': filas[-1][0] + ' ' + filas[-1][2],
        'horas_medidas': round(horas, 1), 'segundos_sin_servicio': tot,
        'duracion_media_s': round(tot / len(filas), 1), 'duracion_min_s': min(dur), 'duracion_max_s': max(dur),
        'periodo_mediana_s': st.median(reg), 'intervalos': len(per), 'intervalos_620_650_s': len(reg),
        'periodo_min_s': min(reg), 'periodo_max_s': max(reg), 'periodo_desv_est_s': round(st.pstdev(reg), 1),
        'router_respondio_pct': round(100 * (1 - gw / fallos), 1),
        'cortes_wan': sum(1 for x in filas if x[6].startswith('WAN')),
        'minutos_por_dia': round(min_dia, 1), 'horas_por_mes': round(min_dia * 30 / 60, 1),
        'excluidos_ruido': ruido, 'excluidos_por_pruebas': n_excl, 'registros_invalidos': n_inv,
        'cortes_por_condicion': {et: sum(1 for x in filas if x[7] == et) for _, et in tramos},
    }
    # Fase de los cortes tras cada restablecimiento de la sesión (reinicio, cambio de modo):
    # segundos hasta el siguiente corte y desfase frente a la cadencia previa (último corte + 633 s).
    fases = []
    for rst in ev.get('restablecimientos', []):
        fin_ev = datetime.strptime(rst['fin'], FMT)
        sig = next(t for t in inicio if t > fin_ev)
        fila = {'fin_evento': rst['fin'], 'siguiente_corte': sig.strftime(FMT),
                'segundos_hasta_siguiente': int((sig - fin_ev).total_seconds())}
        if rst.get('inicio'):
            ini_ev = datetime.strptime(rst['inicio'], FMT)
            esperado = [t for t in inicio if t < ini_ev][-1] + timedelta(seconds=633)
            fila['corte_esperado_con_cadencia_previa'] = esperado.strftime(FMT)
            fila['ocurrio_corte_esperado'] = any(abs((t - esperado).total_seconds()) <= 5 for t in inicio)
            fila['desfase_s'] = int((sig - esperado).total_seconds())
        fases.append(fila)
    resumen['fase_tras_restablecimientos'] = fases

    # Latencia por periodo: latidos 'ok' (uno por minuto), respuestas >= 800 ms y fallos aislados por hora medida.
    lat = []
    for per_l in ev.get('periodos_latencia', []):
        d0, d1 = datetime.strptime(per_l['desde'], FMT), datetime.strptime(per_l['hasta'], FMT)
        sub = [(t, x) for t, x in lineas if d0 <= t < d1]
        ok = sorted(int(re.match(r'ok (\d+)', x).group(1)) for t, x in sub if x.startswith('ok '))
        h = horas_medidas(sub)
        lat.append({'periodo': per_l['nombre'], 'horas_medidas': round(h, 1), 'latidos_ok': len(ok),
                    'latencia_mediana_ms': st.median(ok), 'latencia_p99_ms': ok[int(.99 * len(ok))],
                    'pct_mayor_100_ms': round(100 * sum(v > 100 for v in ok) / len(ok), 1),
                    'lentas_800ms_por_hora': round(sum(1 for t, x in sub if x.startswith('LENTA')) / h, 2),
                    'fallos_aislados_por_hora': round(sum(1 for t, x in sub if x.startswith('OK tras')) / h, 2)})
    resumen['latencia_por_periodo'] = lat

    with open(os.path.join(a.salida, 'resumen.json'), 'w', encoding='utf-8') as f:
        json.dump(resumen, f, ensure_ascii=False, indent=1)
    print(json.dumps(resumen, ensure_ascii=False, indent=1))


if __name__ == '__main__':
    main()
