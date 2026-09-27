# Monitor WAN

Widget de escritorio para Windows (WPF, .NET 11) que vigila la conexión a Internet, registra cada
corte con marca de tiempo al milisegundo y se queda siempre encima de cualquier ventana, incluido
el *picture-in-picture* de Chrome/Edge.

Se hizo para documentar microcortes periódicos de un servicio de internet fijo inalámbrico (FWA)
4G/5G: un CPE que pierde la ruta WAN durante unos segundos a intervalos regulares. La forma en que
se usó para las pruebas está en [`docs/METODOLOGIA.md`](docs/METODOLOGIA.md).

## Qué muestra

- Punto de estado: verde (en línea), ámbar (sin respuesta aislada), rojo (corte en curso) y latencia actual.
- Hora prevista del próximo corte, calculada desde el último corte real más el periodo observado
  (mediana de los intervalos regulares). Si la hora pasa sin corte, lo indica en lugar de correr el reloj.
- Hoy: cortes, tiempo total sin servicio y disponibilidad.
- Último corte (hora, duración, tipo WAN/LAN), periodo estimado y gateway detectado.

## Cómo mide

- Una sonda ICMP por segundo a `8.8.8.8`, con timeout de 2 s.
- Si la sonda falla, hace ping al gateway (el CPE) con timeout de 0,7 s para saber si la red local responde.
- Un **corte** se abre con 2 sondas fallidas consecutivas y se cierra con la primera respuesta.
- **Origen**: *LAN* si en la mitad o más de las sondas fallidas tampoco respondió el gateway (cable, WiFi,
  PC o CPE apagado); *WAN* en caso contrario (el CPE responde, pero no hay salida a Internet).
- Las respuestas de 800 ms o más se registran como `LENTA` (enlace congestionado, no es corte) y los
  fallos sueltos que no llegan a abrir un corte, como fallos aislados.

## Registros

En `%LOCALAPPDATA%\WanMonitor\`:

| Archivo | Contenido |
|---|---|
| `cortes.csv` | Una fila por corte: `fecha;inicio;fin;segundos;tipo;fallos;fallos_gateway;contexto` (las filas de las primeras versiones no tienen las dos últimas columnas) |
| `sondas-AAAA-MM-DD.log` | Arranque del monitor con su configuración, cada sonda fallida, cada respuesta lenta, apertura y cierre de cortes, y un latido `ok` por minuto |
| `nota.txt` | Opcional: etiqueta libre de la configuración vigente (por ejemplo "5G NSA" o "4G"), que se copia en la columna `contexto` |
| `ajustes.json` | Posición, opacidad y modo compacto del widget |

## Análisis

`analisis/generar_anexo.py` toma `cortes.csv` y los `sondas-*.log`, aplica las reglas de exclusión
declaradas en `analisis/eventos.json` y produce `anexo-cortes.csv` y `resumen.json`:

```
python analisis/generar_anexo.py --datos "%LOCALAPPDATA%\WanMonitor" --eventos analisis/eventos.json --salida salida
```

Solo usa la biblioteca estándar de Python 3.

## Uso

- Arrastrar para mover. Doble clic: modo compacto. Clic derecho: menú.
- Menú: pausar la sonda, copiar un resumen, abrir la carpeta de registros, opacidad, iniciar con Windows,
  reiniciar estadísticas, salir.
- La ventana reafirma `HWND_TOPMOST` cada segundo; por eso queda encima del PiP del navegador, que también
  es *topmost*. No aparece en Alt+Tab ni en la barra de tareas.

## Compilar

Requiere el SDK de .NET 11 en Windows (`net11.0-windows`, con `RollForward=LatestMajor`).

```
dotnet run
```

Para un solo `WanMonitor.exe` en `dist\` (necesita el .NET Desktop Runtime instalado):

```
publicar.cmd
```
