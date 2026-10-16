// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using osu.Framework.Graphics.Textures;

namespace osu.Framework.Graphics.Rendering
{
    /// <summary>
    /// Torii: registra la SECUENCIA de cortes de lote por textura dentro de un cuadro.
    /// </summary>
    /// <remarks>
    /// El censo del arbol de dibujo llego hasta aca y no mas: mostro que Torii tiene la
    /// misma cantidad de drawables que vanilla, la misma area, la misma ventana y hasta
    /// menos texto, y aun asi hace 174 draw calls contra 29. Con solo seis texturas en
    /// juego, 122 cortes por textura significa que la MISMA textura se vuelve a atar una y
    /// otra vez, o sea que el orden de dibujo alterna A-B-A-B en vez de agrupar.
    ///
    /// Eso no se ve desde el arbol de drawables, hay que verlo desde el renderer. Esto
    /// guarda que se ato y por que, en orden, para poder leer el patron.
    ///
    /// Ojo con el wrap mode: <see cref="Renderer.setWrapMode"/> tambien corta el lote
    /// cuando cambia, SOBRE LA MISMA TEXTURA. Por eso se registra aparte: un ciclo de
    /// clamp/repeat sobre una sola textura produce exactamente esta huella y no se nota
    /// mirando cuantas texturas hay.
    /// </remarks>
    public static class TextureBindTrace
    {
        private const int max_entradas = 4000;

        private static readonly List<string> entradas = new List<string>(max_entradas);
        private static readonly object candado = new object();

        private static bool? enabled;

        public static bool Enabled => enabled ??= Environment.GetEnvironmentVariable("TORII_DRAW_CENSUS") == "1";

        // volatile: lo prende el hilo de update y lo lee el de dibujo. sin esto el hilo
        // de dibujo puede seguir viendo false y no grabar nada, que es exactamente lo que
        // paso en la primera prueba.
        private static volatile bool recording;

        /// <summary>Se esta grabando ahora mismo.</summary>
        public static bool Recording => recording;

        /// <summary>
        /// De que drawable viene lo que se esta dibujando. Lo setea DrawNode.DrawOther
        /// antes de cada nodo, y lo lee Record para poder atribuir el cambio de textura.
        /// </summary>
        /// <remarks>
        /// Sin esto la traza dice QUE textura se ato pero no QUIEN la pidio, y el problema
        /// es justamente de orden: saber que las mismas dos texturas se alternan no alcanza,
        /// hace falta saber que drawable se mete en el medio y parte el lote.
        /// </remarks>
        public static string CurrentSource;

        /// <summary>Empieza a grabar. Se detiene sola al llegar al tope.</summary>
        public static void Start()
        {
            if (!Enabled)
                return;

            lock (candado)
            {
                entradas.Clear();
                recording = true;
            }
        }

        public static void Record(INativeTexture texture, string motivo)
        {
            if (!Recording)
                return;

            lock (candado)
            {
                if (entradas.Count >= max_entradas)
                {
                    recording = false;
                    return;
                }

                // Identifier sale del nombre de Veldrid y casi siempre viene vacio,
                // asi que no distingue nada. Lo que importa es CUANTAS texturas
                // distintas se alternan y de que tamanio: un atlas es grande
                // (1024x1024 o mas) y una textura suelta es chica. El hash de la
                // instancia muestra si vuelve una y otra vez la misma.
                string id = texture == null
                    ? "(null)"
                    : $"{texture.GetType().Name}#{texture.GetHashCode():x8} {texture.Width}x{texture.Height}";

                entradas.Add($"{motivo}\t{id}\t{CurrentSource ?? "?"}");
            }
        }

        public static void RecordWrap(string motivo)
        {
            if (!Recording)
                return;

            lock (candado)
            {
                if (entradas.Count >= max_entradas)
                {
                    recording = false;
                    return;
                }

                entradas.Add($"{motivo}\t-");
            }
        }

        /// <summary>Escribe lo grabado y para. Devuelve la ruta, o null si no habia nada.</summary>
        public static string Dump(string label)
        {
            List<string> copia;

            lock (candado)
            {
                recording = false;

                if (entradas.Count == 0)
                    return null;

                copia = entradas.ToList();
                entradas.Clear();
            }

            string path = Path.Combine(Path.GetTempPath(), $"texture-trace-{label}-{DateTime.Now:HHmmss}.txt");

            using (var w = new StreamWriter(path))
            {
                w.WriteLine($"# {copia.Count} cortes de lote grabados");
                w.WriteLine();

                w.WriteLine("# por motivo:");

                foreach (var g in copia.GroupBy(x => x.Split('\t')[0]).OrderByDescending(g => g.Count()))
                    w.WriteLine($"{g.Count(),8}  {g.Key}");

                w.WriteLine();
                w.WriteLine("# texturas distintas que participaron:");

                foreach (var g in copia.Select(x => x.Split('\t')[1]).Where(x => x != "-").GroupBy(x => x).OrderByDescending(g => g.Count()))
                    w.WriteLine($"{g.Count(),8}  {g.Key}");

                w.WriteLine();
                w.WriteLine("# quien pidio cada cambio de textura (el que mas aparece es el que parte los lotes):");

                foreach (var g in copia.Select(x => x.Split('	')).Where(p => p.Length > 2).GroupBy(p => p[2]).OrderByDescending(g => g.Count()).Take(15))
                    w.WriteLine($"{g.Count(),8}  {g.Key}");

                w.WriteLine();
                w.WriteLine("# la secuencia, en orden. aca se lee si alterna A-B-A-B:");

                foreach (string e in copia)
                    w.WriteLine(e);
            }

            return path;
        }
    }
}
