# Metodología de las pruebas

Cómo se midieron y analizaron las interrupciones de un servicio de internet fijo inalámbrico 5G (FWA)
entre el 8 y el 26 de septiembre de 2026. Todas las cifras del anexo se regeneran con
`analisis/generar_anexo.py` a partir de los registros crudos y de `analisis/eventos.json`.

## 1. Montaje

- Equipo del operador: CPE modelo C150, firmware `BCWW150.ClaroCo.B03`, en modo de red automático 4G/5G.
- Computador de medición conectado **por cable UTP** al CPE, para descartar el WiFi.
- El monitor de este repositorio corriendo en ese computador. Solo mide mientras el computador está
  encendido; por eso se reportan las horas efectivamente medidas y no días calendario.

## 2. Sonda

- Una sonda ICMP por segundo a `8.8.8.8`.
- Timeout de 1 s hasta el 10/09/2026 17:35 y de 2 s desde entonces, para no contar como corte un enlace
  congestionado que responde lento. Cada arranque del monitor queda en el log con su configuración
  (`INICIO monitor (destino …, intervalo …, timeout …)`).
- Cuando una sonda falla, se hace ping al gateway (el CPE) con timeout de 0,7 s.
- Con timeout de 2 s, una sonda fallida tarda 2 s: por eso un corte de 8 s registra unas 4 sondas fallidas,
  frente a 8 con la versión de 1 s.

## 3. Definiciones

- **Corte**: se abre con 2 sondas fallidas consecutivas y se cierra con la primera respuesta. Duración =
  fin − inicio, en segundos.
- **Origen**: *LAN* si en la mitad o más de las sondas fallidas del corte tampoco respondió el gateway;
  *WAN* si el gateway siguió respondiendo, es decir, la red local funcionaba y lo que se perdió fue la salida
  por el enlace del operador. El análisis recalcula esta regla para todas las filas; en las filas de las
  primeras versiones del CSV, que no traen la columna `fallos_gateway`, la cuenta se toma del log de sondas.
- **Respuesta lenta**: 800 ms o más. Se registra como `LENTA` y no cuenta como corte.
- **Fallo aislado**: una sonda fallida que no llega a abrir un corte.
- **Periodo**: tiempo entre el inicio de dos cortes consecutivos. Se consideran regulares los intervalos de
  620 a 650 s; los demás corresponden a tramos con el monitor apagado o a eventos sueltos.
- **Horas medidas**: suma de los intervalos entre líneas consecutivas del log de hasta 5 minutos. El monitor
  escribe al menos un latido por minuto, así que un hueco mayor significa computador apagado o suspendido
  y no se cuenta.
- **Proyección**: minutos diarios = segundos sin servicio ÷ horas medidas × 24 ÷ 60; horas mensuales =
  minutos diarios × 30 ÷ 60.

## 4. Exclusiones

Declaradas una por una, con su motivo, en `analisis/eventos.json`:

- **Ruido**: eventos de menos de 5 s (saturación momentánea del enlace).
- **Provocados por las pruebas**: cambios de modo de red, desconexiones y reinicios del equipo, y los
  arranques que los siguen.
- **Registros inválidos**: por ejemplo, un "corte" de 7 horas mientras el computador estuvo suspendido.

El anexo contiene únicamente interrupciones no inducidas.

## 5. Pruebas controladas

1. **Combinaciones de radio.** El patrón se observó igual con LTE B7 + NR n78, LTE B28 sin NR y
   LTE B2 + NR n78, según el panel del equipo.
2. **4G forzado.** Entre el 10/09/2026 17:38 y el 11/09/2026 06:56 (13,3 h) el equipo se forzó a 4G
   exclusivamente (banda 28, 10 MHz): mismo periodo y misma duración de los cortes.
3. **Desconexiones y reinicios.** Desconexión de la corriente el 17/09 y el 19/09; reinicios el 24/09 y el
   25/09. El patrón reaparece idéntico después de cada uno.
4. **Fase tras cada restablecimiento.** Para cada evento que restablece la sesión de datos se mide el tiempo
   hasta el siguiente corte y se comprueba si ocurrió el corte que tocaba según la cadencia anterior
   (último corte + 633 s). Resultado: el siguiente corte llega a un ciclo completo de la reconexión y el
   corte esperado no ocurre. En cambio, al pasar de 5G a 4G sin perder la sesión (10/09 17:38) la cadencia
   siguió intacta. Es decir, el temporizador se arma con cada sesión de datos, no con la radio ni con la celda.
   Los eventos analizados están en `restablecimientos` de `eventos.json` y el resultado sale en
   `fase_tras_restablecimientos` de `resumen.json`.
5. **Lecturas de radio.** Se consultó la información de ingeniería del panel web del equipo (tecnología,
   banda, celda, RSRP, RSRQ, SINR): el 11/09 (NR n78 de 80 MHz, RSRP −64 dBm, SINR 34 dB) y el 24/09 a las
   14:54, durante una caída a 4G (solo 4G en banda 28, RSRQ −17 dB, sin NR ni agregación de portadoras).
6. **Configuración del equipo.** Se revisó la configuración accesible al usuario (reinicio programado, modo
   nocturno, vigilancia por ping, DHCP, temperatura, CPU y memoria) y se consultó la actualización de
   firmware contra el servidor del operador, que no reportó versión más reciente.

## 6. Comparación antes y después de las caídas a 4G

Desde el 24/09/2026 14:17 el equipo cae de forma intermitente a 4G en banda 28, sin 5G, en episodios de alrededor
de una hora. La latencia antes y después de esa fecha se compara con los latidos
`ok` del log (uno por minuto), las respuestas de 800 ms o más y los fallos aislados, normalizados por hora
medida. Periodos en `periodos_latencia` de `eventos.json`; resultado en `latencia_por_periodo`.

## 7. Limitaciones

- La sonda va a un solo destino público. Durante los cortes el propio CPE devolvió «red de destino
  inaccesible», lo que indica que la ruta se perdió en el equipo y no en el destino.
- Solo se mide con el computador encendido; los resultados se expresan por hora medida.
- La latencia en condiciones normales se muestrea una vez por minuto; las respuestas lentas y los fallos
  se registran todos.

## 8. Reproducir

```
python analisis/generar_anexo.py --datos <carpeta de registros> --eventos analisis/eventos.json --salida salida
```

Produce `anexo-cortes.csv` y `resumen.json`. Los registros crudos (`cortes.csv` y `sondas-*.log`) están
disponibles a solicitud.
