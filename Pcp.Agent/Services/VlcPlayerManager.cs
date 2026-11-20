using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;
using LibVLCSharp.Shared;
using LibVLCSharp.WinForms;
using Microsoft.Extensions.Configuration;

namespace Pcp.Agent.Services;

/// <summary>
/// Administrador del reproductor multimedia basado en LibVLCSharp para Windows.
/// Gestiona una ventana de pantalla completa en modo kiosco (sin bordes, sin cursor)
/// con aceleración por hardware y generación periódica de fotogramas (thumbnails).
/// </summary>
public class VlcPlayerManager : IDisposable
{
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
    private bool _isDisposed;

    public string CurrentMedia => _currentMedia;
    public int Volume => _volume;
    public bool IsPlaying => _mediaPlayer?.IsPlaying ?? false;

    public VlcPlayerManager(IConfiguration configuration)
    {
        _devMode = configuration.GetValue<bool>("Agent:DevMode");
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
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"[AVISO VLC] Core.Initialize advirtió: {coreEx.Message}. Continuando...");
                Console.ResetColor();
            }

            // Parámetros de aceleración por hardware limpios y compatibles con LibVLC en Windows
            try
            {
                _libVLC = new LibVLC("--avcodec-hw=any", "--no-video-title-show", "--quiet");
                _mediaPlayer = new MediaPlayer(_libVLC);
            }
            catch (Exception vlcEx)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"[AVISO VLC] No se pudo instanciar LibVLC nativo ({vlcEx.Message}). El agente continuará en modo simulado.");
                Console.ResetColor();
                _libVLC = null;
                _mediaPlayer = null;
            }

            // Crear ventana de reproducción:
            // - Producción: kiosco a pantalla completa, sin bordes, siempre al frente.
            // - Desarrollo: ventana normal redimensionable y movable, sin TopMost.
            _kioskForm = new Form
            {
                Text = _devMode ? "PCP Kiosk Player (DEV)" : "PCP Kiosk Player",
                FormBorderStyle = _devMode ? FormBorderStyle.Sizable : FormBorderStyle.None,
                WindowState = _devMode ? FormWindowState.Normal : FormWindowState.Maximized,
                TopMost = !_devMode,
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
            }

            _kioskForm.Load += (s, e) =>
            {
                if (!_devMode)
                {
                    try { Cursor.Hide(); } catch { }
                }

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
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[AVISO KIOSK UI] Ventana de reproducción no disponible ({ex.Message}). Agente operativo en modo fondo.");
            Console.ResetColor();
        }
        finally
        {
            _readyEvent.Set();
        }
    }

    /// <summary>
    /// Inicia la reproducción de un archivo o URL multimedia.
    /// </summary>
    public void Play(string mediaPath, bool loop = false, int volume = 80)
    {
        _volume = Math.Clamp(volume, 0, 100);
        _currentMedia = mediaPath;

        if (_libVLC == null || _mediaPlayer == null) return;

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

            // Asegurar que la ventana esté visible al reproducir
            if (_kioskForm != null && _kioskForm.IsHandleCreated)
            {
                _kioskForm.Invoke(() =>
                {
                    _kioskForm.Visible = true;
                    _kioskForm.BringToFront();
                });
            }
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[VLC PLAY ERROR] {ex.Message}");
            Console.ResetColor();
        }
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
    /// Detiene la reproducción y regresa a reposo.
    /// </summary>
    public void Stop()
    {
        _currentMedia = "idle";
        if (_mediaPlayer != null)
        {
            _mediaPlayer.Stop();
        }
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
                Console.WriteLine($"[SNAPSHOT ERROR] {ex.Message}");
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
