using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;
using LibVLCSharp.Shared;
using LibVLCSharp.WinForms;
using Microsoft.Extensions.Configuration;
using Pcp.Agent.Logging;

namespace Pcp.Agent.Services;

/// <summary>
/// Control mínimo de la ventana del kiosco. Lo implementa <see cref="VlcPlayerManager"/>
/// y lo consume el control remoto para liberar el escritorio mientras dura el VNC.
/// </summary>
public interface IKioskWindowController
{
    /// <summary>Suspende el kiosco (minimiza la ventana, quita TopMost y libera el cursor).</summary>
    void Suspend(string reason);

    /// <summary>Reanuda el kiosco cuando ya no queda ningún motivo de suspensión.</summary>
    void Resume(string reason);
}

/// <summary>
/// Administrador del reproductor multimedia basado en LibVLCSharp para Windows.
///
/// Máquina de estados de la ventana (siempre aplicada en el hilo STA de la UI):
///   - <c>Kiosk</c>: hay un medio cargado → maximizada, sin bordes, TopMost y cursor oculto.
///   - <c>Idle</c>: sin medio → pantalla completa según <c>Agent:Kiosk:IdleWindowState</c>,
///     pero NUNCA TopMost (no secuestra el escritorio).
///   - <c>Suspended</c>: hay una sesión de VNC o una orden manual → normal/minimizada,
///     en la barra de tareas, sin TopMost y con el cursor visible.
///
/// La suspensión usa un conjunto de motivos ("vnc", "manual") para que una orden del
/// panel y una sesión de control remoto no se reanuden entre sí por error.
/// </summary>
public class VlcPlayerManager : IKioskWindowController, IDisposable
{
    /// <summary>Motivo de suspensión usado por el túnel de escritorio remoto.</summary>
    public const string VncSuspendReason = "vnc";

    /// <summary>Motivo de suspensión usado por las órdenes manuales del panel.</summary>
    public const string ManualSuspendReason = "manual";

    private LibVLC? _libVLC;
    private MediaPlayer? _mediaPlayer;
    private VideoView? _videoView;
    private Form? _kioskForm;
    private readonly Thread _uiThread;
    private readonly ManualResetEventSlim _readyEvent = new(false);

    private string _currentMedia = "idle";
    private int _volume = 80;
    private readonly string _tempSnapshotPath;
    private readonly bool _devMode;
    private readonly bool _topMostWhilePlaying;
    private readonly string _idleWindowState;
    private bool _isDisposed;

    private readonly HashSet<string> _suspendReasons = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _stateLock = new();
    private volatile bool _hasMedia;
    private bool _cursorHidden;
    private bool _suspendedLogged;

    public string CurrentMedia => _currentMedia;
    public int Volume => _volume;
    public bool IsPlaying => _mediaPlayer?.IsPlaying ?? false;

    /// <summary>Indica si hay un medio cargado (reproduciendo o en pausa).</summary>
    public bool HasMedia => _hasMedia;

    /// <summary>Indica si el kiosco está suspendido por VNC o por una orden manual.</summary>
    public bool IsKioskSuspended
    {
        get
        {
            lock (_stateLock)
            {
                return _suspendReasons.Count > 0;
            }
        }
    }

    public VlcPlayerManager(IConfiguration configuration)
    {
        _devMode = configuration.GetValue<bool>("Agent:DevMode");
        _topMostWhilePlaying = configuration.GetValue("Agent:Kiosk:TopMostOnlyWhilePlaying", true);
        _idleWindowState = (configuration["Agent:Kiosk:IdleWindowState"] ?? "Maximized")
            .Trim()
            .ToLowerInvariant();
        _tempSnapshotPath = Path.Combine(Path.GetTempPath(), $"pcp_snap_{Guid.NewGuid():N}.jpg");

        // Hilo STA dedicado a la ventana de reproducción WinForms
        _uiThread = new Thread(InitializeUi);
        _uiThread.SetApartmentState(ApartmentState.STA);
        _uiThread.IsBackground = true;
        _uiThread.Start();

        // Esperar hasta 5 segundos a que la ventana y LibVLC estén inicializados
        _readyEvent.Wait(TimeSpan.FromSeconds(5));
    }

    private void InitializeUi()
    {
        try
        {
            // Inicializar core nativo de LibVLC (VideoLAN.LibVLC.Windows)
            try
            {
                Core.Initialize();
            }
            catch (Exception coreEx)
            {
                AgentLog.Warn($"[AVISO VLC] Core.Initialize advirtió: {coreEx.Message}. Continuando...");
            }

            // Parámetros de aceleración por hardware limpios y compatibles con LibVLC en Windows
            try
            {
                _libVLC = new LibVLC("--avcodec-hw=any", "--no-video-title-show", "--quiet");
                _mediaPlayer = new MediaPlayer(_libVLC);
            }
            catch (Exception vlcEx)
            {
                AgentLog.Warn($"[AVISO VLC] No se pudo instanciar LibVLC nativo ({vlcEx.Message}). El agente continuará en modo simulado.");
                _libVLC = null;
                _mediaPlayer = null;
            }

            // Crear ventana de reproducción:
            // - Producción: kiosco sin bordes. Arranca maximizada pero SIN TopMost:
            //   el modo "siempre al frente" solo se aplica cuando hay vídeo (ApplyWindowMode).
            // - Desarrollo: ventana normal redimensionable y movable, sin TopMost.
            _kioskForm = new Form
            {
                Text = _devMode ? "PCP Kiosk Player (DEV)" : "PCP Kiosk Player",
                FormBorderStyle = _devMode ? FormBorderStyle.Sizable : FormBorderStyle.None,
                WindowState = _devMode ? FormWindowState.Normal : FormWindowState.Maximized,
                TopMost = false,
                BackColor = Color.Black,
                ShowInTaskbar = _devMode,
                StartPosition = FormStartPosition.CenterScreen
            };

            if (_devMode)
            {
                _kioskForm.ClientSize = new Size(960, 540);
                _kioskForm.MinimumSize = new Size(480, 270);
            }

            if (_mediaPlayer != null)
            {
                _videoView = new VideoView
                {
                    Dock = DockStyle.Fill,
                    BackColor = Color.Black,
                    MediaPlayer = _mediaPlayer
                };
                _kioskForm.Controls.Add(_videoView);
                SubscribePlayerEvents(_mediaPlayer);
            }

            _kioskForm.Load += (s, e) =>
            {
                // Aplica el modo efectivo: idle (sin TopMost) o suspendido si el VNC llegó antes.
                ApplyWindowModeCore();
                WriteKioskLog($"Ventana lista en modo {DescribeMode()}.");
                _readyEvent.Set();
            };

            // Tecla Escape para salir de pantalla completa (solo en modo kiosco)
            _kioskForm.KeyDown += (s, e) =>
            {
                if (!_devMode && e.KeyCode == Keys.Escape)
                {
                    _kioskForm.WindowState = _kioskForm.WindowState == FormWindowState.Maximized
                        ? FormWindowState.Normal
                        : FormWindowState.Maximized;
                }
            };

            Application.Run(_kioskForm);
        }
        catch (Exception ex)
        {
            AgentLog.Warn($"[AVISO KIOSK UI] Ventana de reproducción no disponible ({ex.Message}). Agente operativo en modo fondo.");
        }
        finally
        {
            _readyEvent.Set();
        }
    }

    /// <summary>
    /// Mantiene sincronizado el estado "hay medio" aunque el vídeo falle o termine sin loop.
    /// Los eventos llegan desde hilos de LibVLC: el cambio de ventana se marshalea a la UI.
    /// </summary>
    private void SubscribePlayerEvents(MediaPlayer player)
    {
        player.Playing += (s, e) =>
        {
            _hasMedia = true;
            ApplyWindowMode();
        };

        player.Paused += (s, e) =>
        {
            // Una pausa sigue mostrando el medio en pantalla: el kiosco se mantiene.
            _hasMedia = true;
            ApplyWindowMode();
        };

        player.Stopped += (s, e) =>
        {
            // "Stopped" también se dispara al cambiar de medio: solo se degrada a idle
            // cuando el comando stop ya marcó el estado como tal.
            if (string.Equals(_currentMedia, "idle", StringComparison.Ordinal))
            {
                _hasMedia = false;
                ApplyWindowMode();
            }
        };

        player.EndReached += (s, e) =>
        {
            // Sin loop el medio termina: vuelve a reposo y el kiosco deja de ser TopMost.
            _currentMedia = "idle";
            _hasMedia = false;
            ApplyWindowMode();
        };

        player.EncounteredError += (s, e) =>
        {
            _hasMedia = false;
            ApplyWindowMode();
        };
    }

    /// <summary>
    /// Inicia la reproducción de un archivo o URL multimedia.
    /// </summary>
    public void Play(string mediaPath, bool loop = false, int volume = 80)
    {
        _volume = Math.Clamp(volume, 0, 100);
        _currentMedia = mediaPath;

        if (_libVLC == null || _mediaPlayer == null)
        {
            // Modo degradado: no hay vídeo real, la ventana permanece en reposo.
            _hasMedia = false;
            ApplyWindowMode();
            return;
        }

        try
        {
            // Determinar si es URL web o archivo local
            Media media;
            if (Uri.TryCreate(mediaPath, UriKind.Absolute, out var uriResult) &&
                (uriResult.Scheme == Uri.UriSchemeHttp || uriResult.Scheme == Uri.UriSchemeHttps || uriResult.Scheme == "rtsp"))
            {
                media = new Media(_libVLC, uriResult);
            }
            else
            {
                // Archivo local
                var fullPath = Path.GetFullPath(mediaPath);
                media = new Media(_libVLC, fullPath, FromType.FromPath);
            }

            if (loop)
            {
                media.AddOption(":input-repeat=-1");
            }

            _mediaPlayer.Media = media;
            _mediaPlayer.Volume = _volume;
            _mediaPlayer.Play();

            _hasMedia = true;
        }
        catch (Exception ex)
        {
            _hasMedia = false;
            AgentLog.Error($"[VLC PLAY ERROR] {ex.Message}");
        }

        // No se fuerza BringToFront aquí: si el kiosco está suspendido (VNC activo),
        // el vídeo sigue reproduciéndose minimizado para no tapar el escritorio.
        ApplyWindowMode();
    }

    /// <summary>
    /// Alterna entre pausa y reanudación de la reproducción.
    /// </summary>
    public void Pause()
    {
        if (_mediaPlayer != null)
        {
            if (_mediaPlayer.IsPlaying)
            {
                _mediaPlayer.Pause();
            }
            else
            {
                _mediaPlayer.Play();
            }
        }
    }

    /// <summary>
    /// Detiene la reproducción y regresa a reposo (la ventana deja de ser TopMost).
    /// </summary>
    public void Stop()
    {
        _currentMedia = "idle";
        _hasMedia = false;
        if (_mediaPlayer != null)
        {
            _mediaPlayer.Stop();
        }

        ApplyWindowMode();
    }

    /// <summary>
    /// Modifica el volumen de reproducción (0-100).
    /// </summary>
    public void SetVolume(int volume)
    {
        _volume = Math.Clamp(volume, 0, 100);
        if (_mediaPlayer != null)
        {
            _mediaPlayer.Volume = _volume;
        }
    }

    /// <summary>
    /// Suspende el kiosco: minimiza la ventana, quita TopMost, la muestra en la barra de
    /// tareas y devuelve el cursor. Idempotente por motivo.
    /// </summary>
    public void Suspend(string reason)
    {
        var key = NormalizeReason(reason);

        bool changed;
        lock (_stateLock)
        {
            changed = _suspendReasons.Add(key);
        }

        if (!changed)
        {
            return;
        }

        WriteKioskLog($"Suspensión solicitada ({key}). Motivos activos: {DescribeReasons()}.");
        ApplyWindowMode();
    }

    /// <summary>
    /// Reanuda el kiosco solo cuando no queda ningún motivo de suspensión activo.
    /// </summary>
    public void Resume(string reason)
    {
        var key = NormalizeReason(reason);

        bool changed;
        lock (_stateLock)
        {
            changed = _suspendReasons.Remove(key);
        }

        if (!changed)
        {
            return;
        }

        WriteKioskLog($"Suspensión liberada ({key}). Motivos activos: {DescribeReasons()}.");
        ApplyWindowMode();
    }

    private static string NormalizeReason(string? reason)
        => string.IsNullOrWhiteSpace(reason) ? ManualSuspendReason : reason.Trim();

    private string DescribeReasons()
    {
        lock (_stateLock)
        {
            return _suspendReasons.Count == 0 ? "ninguno" : string.Join(", ", _suspendReasons);
        }
    }

    private string DescribeMode()
    {
        if (IsKioskSuspended) return "suspendido";
        return _hasMedia ? "kiosco" : "reposo";
    }

    /// <summary>
    /// Aplica el modo de ventana efectivo. Se puede llamar desde cualquier hilo:
    /// el cambio real se ejecuta en el hilo STA mediante BeginInvoke.
    /// </summary>
    private void ApplyWindowMode()
    {
        RunOnUi(ApplyWindowModeCore);
    }

    /// <summary>
    /// Debe ejecutarse siempre en el hilo de la UI.
    /// </summary>
    private void ApplyWindowModeCore()
    {
        var form = _kioskForm;
        if (form == null || form.IsDisposed || !form.IsHandleCreated)
        {
            return;
        }

        var suspended = IsKioskSuspended;

        try
        {
            if (_devMode)
            {
                // En desarrollo nunca se secuestra el escritorio: ventana normal y sin TopMost.
                form.TopMost = false;
                SetCursorHidden(false);
                if (form.WindowState == FormWindowState.Minimized)
                {
                    form.WindowState = FormWindowState.Normal;
                }
                return;
            }

            if (suspended)
            {
                // 1) Devolver el escritorio al operador VNC.
                form.TopMost = false;
                form.ShowInTaskbar = true;

                // Solo se normaliza cuando de verdad hay que cambiar el marco; si ya está
                // minimizada no se toca (evita parpadeos con cada evento del reproductor).
                if (form.WindowState != FormWindowState.Minimized)
                {
                    if (form.WindowState != FormWindowState.Normal)
                    {
                        form.WindowState = FormWindowState.Normal;
                    }

                    // Con borde mientras está suspendida: permite Alt+Tab y restaurarla.
                    form.FormBorderStyle = FormBorderStyle.Sizable;
                    form.WindowState = FormWindowState.Minimized;
                }
                else
                {
                    // El estilo se aplica al restaurar la ventana, sin mostrarla ahora.
                    form.FormBorderStyle = FormBorderStyle.Sizable;
                }

                SetCursorHidden(false);

                if (!_suspendedLogged)
                {
                    _suspendedLogged = true;
                    WriteKioskLog("Kiosco minimizado: el escritorio queda libre para el control remoto.");
                }

                return;
            }

            // 2) Restaurar la ventana del kiosco.
            if (form.WindowState == FormWindowState.Minimized)
            {
                form.WindowState = FormWindowState.Normal;
            }

            form.FormBorderStyle = FormBorderStyle.None;
            form.ShowInTaskbar = false;
            form.Visible = true;

            var targetState = _hasMedia ? FormWindowState.Maximized : IdleFormState();
            form.WindowState = targetState;

            // Requisito clave: TopMost SOLO con un medio cargado.
            form.TopMost = _hasMedia && _topMostWhilePlaying;

            SetCursorHidden(targetState != FormWindowState.Minimized);

            if (_hasMedia)
            {
                form.BringToFront();
            }

            if (_suspendedLogged)
            {
                _suspendedLogged = false;
                WriteKioskLog($"Kiosco restaurado en modo {(_hasMedia ? "kiosco" : "reposo")}.");
            }
        }
        catch (Exception ex)
        {
            AgentLog.Warn($"[KIOSK AVISO] No se pudo aplicar el modo de ventana: {ex.Message}");
        }
    }

    private FormWindowState IdleFormState()
        => _idleWindowState switch
        {
            "normal" => FormWindowState.Normal,
            "minimized" => FormWindowState.Minimized,
            _ => FormWindowState.Maximized
        };

    /// <summary>
    /// Oculta o muestra el cursor del kiosco sin desbalancear el contador de ShowCursor.
    /// Solo se llama desde el hilo de la UI.
    /// </summary>
    private void SetCursorHidden(bool hidden)
    {
        if (_cursorHidden == hidden)
        {
            return;
        }

        try
        {
            if (hidden)
            {
                Cursor.Hide();
            }
            else
            {
                Cursor.Show();
            }

            _cursorHidden = hidden;
        }
        catch
        {
            // El cursor del sistema no es crítico.
        }
    }

    private static void WriteKioskLog(string message)
    {
        AgentLog.Info($"[KIOSK] {message}");
    }

    /// <summary>Ejecuta una acción en el hilo STA de la ventana, sin bloquear al llamador.</summary>
    private void RunOnUi(Action action)
    {
        var form = _kioskForm;
        if (form == null || form.IsDisposed || !form.IsHandleCreated)
        {
            return;
        }

        try
        {
            if (form.InvokeRequired)
            {
                form.BeginInvoke(action);
            }
            else
            {
                action();
            }
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    /// <summary>
    /// Captura un fotograma del video en reproducción o genera una imagen de reposo en formato Base64.
    /// Resolución recomendada para thumbnails: 320x180 px.
    /// </summary>
    public string CaptureThumbnailBase64(string screenId)
    {
        // 1. Si hay video en reproducción, capturar fotograma con LibVLC
        if (_mediaPlayer != null && _mediaPlayer.IsPlaying)
        {
            try
            {
                if (File.Exists(_tempSnapshotPath))
                {
                    File.Delete(_tempSnapshotPath);
                }

                // Solicitar snapshot de 320x180 píxeles a LibVLC
                var success = _mediaPlayer.TakeSnapshot(0, _tempSnapshotPath, 320, 180);

                if (success)
                {
                    // Dar un breve tiempo para que el decodificador vuelque el archivo
                    for (int i = 0; i < 6; i++)
                    {
                        if (File.Exists(_tempSnapshotPath) && new FileInfo(_tempSnapshotPath).Length > 0)
                        {
                            var bytes = File.ReadAllBytes(_tempSnapshotPath);
                            File.Delete(_tempSnapshotPath);
                            return $"data:image/jpeg;base64,{Convert.ToBase64String(bytes)}";
                        }
                        Thread.Sleep(50);
                    }
                }
            }
            catch (Exception ex)
            {
                AgentLog.Error($"[SNAPSHOT ERROR] {ex.Message}");
            }
        }

        // 2. Si no hay video o falló la captura nativa, generar imagen de standby / reposo
        return GenerateStandbyThumbnailBase64(screenId, _currentMedia);
    }

    /// <summary>
    /// Genera una imagen Bitmap sintética de alta calidad (320x180) representando el estado de reposo / standby.
    /// </summary>
    private static string GenerateStandbyThumbnailBase64(string screenId, string currentMedia)
    {
        const int width = 320;
        const int height = 180;

        using var bmp = new Bitmap(width, height);
        using var g = Graphics.FromImage(bmp);

        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        // Fondo oscuro degradado
        using (var brush = new LinearGradientBrush(
            new Point(0, 0), new Point(width, height),
            Color.FromArgb(15, 23, 42), Color.FromArgb(2, 6, 23)))
        {
            g.FillRectangle(brush, 0, 0, width, height);
        }

        // Borde interior sutil
        using (var borderPen = new Pen(Color.FromArgb(51, 65, 85), 1))
        {
            g.DrawRectangle(borderPen, 1, 1, width - 2, height - 2);
        }

        // Título del sistema
        using (var fontTitle = new Font("Segoe UI", 12, FontStyle.Bold))
        using (var brushTitle = new SolidBrush(Color.FromArgb(241, 245, 249)))
        {
            var title = "PCP KIOSK";
            var size = g.MeasureString(title, fontTitle);
            g.DrawString(title, fontTitle, brushTitle, (width - size.Width) / 2, 35);
        }

        // Estado: Reposo / Standby
        using (var fontSub = new Font("Segoe UI", 9, FontStyle.Regular))
        using (var brushSub = new SolidBrush(Color.FromArgb(148, 163, 184)))
        {
            var statusText = currentMedia == "idle" ? "EN REPOSO (STANDBY)" : $"PAUSADO: {Path.GetFileName(currentMedia)}";
            var size = g.MeasureString(statusText, fontSub);
            g.DrawString(statusText, fontSub, brushSub, (width - size.Width) / 2, 65);
        }

        // Identificador de pantalla y hora
        using (var fontFoot = new Font("Consolas", 8, FontStyle.Regular))
        using (var brushFoot = new SolidBrush(Color.FromArgb(94, 234, 212)))
        {
            var footText = $"ID: {screenId} | {DateTime.Now:HH:mm:ss}";
            var size = g.MeasureString(footText, fontFoot);
            g.DrawString(footText, fontFoot, brushFoot, (width - size.Width) / 2, 125);
        }

        // Indicador punto verde de funcionamiento
        using (var dotBrush = new SolidBrush(Color.FromArgb(34, 197, 94)))
        {
            g.FillEllipse(dotBrush, 12, 12, 8, 8);
        }

        // Exportar a JPEG en memoria
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Jpeg);
        return $"data:image/jpeg;base64,{Convert.ToBase64String(ms.ToArray())}";
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        try
        {
            _mediaPlayer?.Stop();
            _mediaPlayer?.Dispose();
            _libVLC?.Dispose();

            if (_kioskForm != null && _kioskForm.IsHandleCreated)
            {
                _kioskForm.Invoke(() => _kioskForm.Close());
            }

            if (File.Exists(_tempSnapshotPath))
            {
                File.Delete(_tempSnapshotPath);
            }
        }
        catch
        {
            // Limpieza sin bloquear
        }
    }
}
