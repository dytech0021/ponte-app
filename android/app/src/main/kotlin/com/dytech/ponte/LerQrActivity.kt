package com.dytech.ponte

import android.Manifest
import android.annotation.SuppressLint
import android.app.Activity
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.ImageFormat
import android.graphics.Matrix
import android.graphics.Paint
import android.graphics.Path
import android.graphics.RectF
import android.graphics.SurfaceTexture
import android.graphics.Typeface
import android.hardware.camera2.CameraAccessException
import android.hardware.camera2.CameraCaptureSession
import android.hardware.camera2.CameraCharacteristics
import android.hardware.camera2.CameraDevice
import android.hardware.camera2.CameraManager
import android.hardware.camera2.CameraMetadata
import android.hardware.camera2.CaptureRequest
import android.hardware.display.DisplayManager
import android.media.ImageReader
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.HandlerThread
import android.os.Looper
import android.provider.Settings
import android.util.Size
import android.util.TypedValue
import android.view.Gravity
import android.view.Surface
import android.view.TextureView
import android.view.View
import android.view.ViewGroup
import android.view.WindowManager
import android.widget.Button
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.TextView
import ponte.core.QrReader
import ponte.core.StreamLink
import kotlin.math.abs
import kotlin.math.max
import kotlin.math.min

/**
 * Lê pela câmera o QR Code de uma transmissão de tela (o da janela "Transmitir a tela" do Ponte no computador) e abre
 * a transmissão aqui no aplicativo. A câmera só fica ligada com esta tela à vista, e a imagem não sai do aparelho: a
 * leitura é feita aqui mesmo.
 */
class LerQrActivity : Activity() {
    private lateinit var preview: TextureView
    private lateinit var message: TextView
    private lateinit var action: Button
    private val main = Handler(Looper.getMainLooper())

    // A câmera, sob lock (a linha da câmera e a da tela mexem nela).
    private val lock = Any()
    private var camera: CameraDevice? = null
    private var session: CameraCaptureSession? = null
    private var reader: ImageReader? = null
    private var previewSurface: Surface? = null
    private var worker: HandlerThread? = null
    private var previewSize: Size? = null
    private var sensorOrientation = 90
    private var started = false
    private var generation = 0   // muda a cada vez que a câmera fecha: respostas atrasadas da câmera anterior são ignoradas
    private var denied = false
    private var done = false
    private var lastText: String? = null
    private var luminance = ByteArray(0)   // só usado na linha da câmera

    private val instructions = "Aponte a câmera para o QR Code da transmissão.\nNo computador, ele aparece na janela \"Transmitir a tela\" do Ponte."

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        val density = resources.displayMetrics.density
        fun dp(value: Int) = (value * density).toInt()

        preview = TextureView(this).apply {
            surfaceTextureListener = object : TextureView.SurfaceTextureListener {
                override fun onSurfaceTextureAvailable(texture: SurfaceTexture, width: Int, height: Int) = startCamera()
                override fun onSurfaceTextureSizeChanged(texture: SurfaceTexture, width: Int, height: Int) = configureTransform()
                override fun onSurfaceTextureDestroyed(texture: SurfaceTexture): Boolean {
                    stopCamera()
                    return true
                }
                override fun onSurfaceTextureUpdated(texture: SurfaceTexture) {}
            }
        }
        message = TextView(this).apply {
            id = R.id.mensagem
            setTextColor(Color.WHITE)
            setTextSize(TypedValue.COMPLEX_UNIT_SP, 16f)
            gravity = Gravity.CENTER
            setShadowLayer(4f, 0f, 1f, Color.BLACK)
            text = instructions
        }
        action = Button(this, null, 0, R.style.BotaoPrincipal).apply {
            id = R.id.acao
            visibility = View.GONE
            // Criado só com o estilo, o botão não herda o centro nem a fonte dos botões do tema.
            gravity = Gravity.CENTER
            typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL)
            setPadding(dp(24), 0, dp(24), 0)
        }
        val panel = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            gravity = Gravity.CENTER_HORIZONTAL
            setPadding(dp(20), dp(16), dp(20), dp(24))
            addView(message, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT))
            addView(action, LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply { topMargin = dp(14) })
        }
        val root = FrameLayout(this).apply {
            setBackgroundColor(Color.BLACK)
            addView(preview, FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT))
            addView(Mira(this@LerQrActivity), FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT))
            addView(panel, FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT, Gravity.BOTTOM))
        }
        // O texto fica acima da barra de navegação, mesmo com a imagem da câmera por trás dela.
        root.setOnApplyWindowInsetsListener { _, insets ->
            @Suppress("DEPRECATION")
            panel.setPadding(dp(20), dp(16), dp(20), dp(24) + insets.systemWindowInsetBottom)
            insets
        }
        setContentView(root)

        if (!hasPermission() && savedInstanceState == null) requestCamera()
    }

    override fun onResume() {
        super.onResume()
        getSystemService(DisplayManager::class.java).registerDisplayListener(rotation, main)
        if (hasPermission()) startCamera()
        else if (denied) askPermission()
    }

    override fun onPause() {
        getSystemService(DisplayManager::class.java).unregisterDisplayListener(rotation)
        stopCamera()
        super.onPause()
    }

    override fun onDestroy() {
        main.removeCallbacksAndMessages(null)
        super.onDestroy()
    }

    // Girar o aparelho de um lado para o outro (de 180°) não recria a tela: a imagem é acertada aqui.
    private val rotation = object : DisplayManager.DisplayListener {
        override fun onDisplayAdded(displayId: Int) {}
        override fun onDisplayRemoved(displayId: Int) {}
        override fun onDisplayChanged(displayId: Int) = configureTransform()
    }

    // ---------- permissão ----------

    private fun hasPermission() = checkSelfPermission(Manifest.permission.CAMERA) == PackageManager.PERMISSION_GRANTED

    private fun requestCamera() {
        requestPermissions(arrayOf(Manifest.permission.CAMERA), PEDIDO_CAMERA)
    }

    override fun onRequestPermissionsResult(requestCode: Int, permissions: Array<out String>, grantResults: IntArray) {
        if (requestCode != PEDIDO_CAMERA) return
        denied = grantResults.firstOrNull() != PackageManager.PERMISSION_GRANTED
        if (denied) askPermission() else startCamera()
    }

    private fun askPermission() {
        show("O Ponte precisa da câmera para ler o QR Code da transmissão. A imagem não sai do aparelho.", "Permitir a câmera") {
            // Depois de "não perguntar de novo", só pelas configurações do Android.
            if (shouldShowRequestPermissionRationale(Manifest.permission.CAMERA)) requestCamera()
            else try {
                startActivity(Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, Uri.fromParts("package", packageName, null)))
            } catch (e: RuntimeException) {
                requestCamera()
            }
        }
    }

    // ---------- câmera ----------

    /**
     * Liga a câmera. Abrir e configurar a câmera pode levar segundos e trava quem pede, então as respostas da câmera
     * e a leitura das imagens rodam na linha "ponte-camera"; a tela só recebe o resultado. O que as duas linhas
     * compartilham fica sob [lock].
     */
    @SuppressLint("MissingPermission")
    private fun startCamera() {
        if (started || done || !hasPermission()) return
        val manager = getSystemService(CameraManager::class.java)
        val chosen = try {
            chooseCamera(manager)
        } catch (e: Exception) {
            null
        }
        if (chosen == null) {
            show("Não foi possível usar a câmera deste aparelho.\nVocê pode colar o link da transmissão no campo de endereço da tela inicial.")
            return
        }
        val (id, characteristics, size) = chosen
        // A imagem da câmera só existe depois que a área de exibição fica pronta (onSurfaceTextureAvailable chama de novo).
        val texture = preview.surfaceTexture ?: return
        sensorOrientation = characteristics.get(CameraCharacteristics.SENSOR_ORIENTATION) ?: 90
        previewSize = size
        texture.setDefaultBufferSize(size.width, size.height)
        configureTransform()

        val thread = HandlerThread("ponte-camera").apply { start() }
        val handler = Handler(thread.looper)
        val images = ImageReader.newInstance(size.width, size.height, ImageFormat.YUV_420_888, 2)
        images.setOnImageAvailableListener({ decode(it) }, handler)
        val surface = Surface(texture)
        val focus = characteristics.get(CameraCharacteristics.CONTROL_AF_AVAILABLE_MODES)?.let { modes ->
            listOf(CameraMetadata.CONTROL_AF_MODE_CONTINUOUS_PICTURE, CameraMetadata.CONTROL_AF_MODE_CONTINUOUS_VIDEO).firstOrNull { it in modes }
        }
        started = true
        val current = synchronized(lock) {
            worker = thread
            reader = images
            previewSurface = surface
            generation
        }

        val callback = object : CameraDevice.StateCallback() {
            override fun onOpened(device: CameraDevice) {
                synchronized(lock) {
                    if (current != generation) {
                        device.close()
                        return
                    }
                    camera = device
                }
                configure(device, surface, images, focus, handler, current)
            }

            override fun onDisconnected(device: CameraDevice) {
                device.close()
                failed(current, "Outro aplicativo pegou a câmera.")
            }

            override fun onError(device: CameraDevice, error: Int) {
                device.close()
                failed(current, when (error) {
                    ERROR_CAMERA_IN_USE, ERROR_MAX_CAMERAS_IN_USE -> "A câmera está sendo usada por outro aplicativo."
                    ERROR_CAMERA_DISABLED -> "A câmera está desativada neste aparelho."
                    else -> "A câmera parou de responder (erro $error)."
                })
            }
        }
        try {
            manager.openCamera(id, callback, handler)
        } catch (e: Exception) {
            failed(current, "Não foi possível abrir a câmera.")
        }
    }

    // Roda na linha da câmera.
    private fun configure(device: CameraDevice, surface: Surface, images: ImageReader, focus: Int?, handler: Handler, current: Int) {
        val callback = object : CameraCaptureSession.StateCallback() {
            override fun onConfigured(configured: CameraCaptureSession) {
                synchronized(lock) {
                    if (current != generation) {
                        configured.close()
                        return
                    }
                    session = configured
                }
                try {
                    val request = device.createCaptureRequest(CameraDevice.TEMPLATE_PREVIEW).apply {
                        addTarget(surface)
                        addTarget(images.surface)
                        set(CaptureRequest.CONTROL_MODE, CameraMetadata.CONTROL_MODE_AUTO)
                        if (focus != null) set(CaptureRequest.CONTROL_AF_MODE, focus)
                    }.build()
                    configured.setRepeatingRequest(request, null, handler)
                    main.post { if (current == generation) show(instructions) }
                } catch (e: Exception) {
                    failed(current, "A câmera parou de responder.")
                }
            }

            override fun onConfigureFailed(failed: CameraCaptureSession) {
                failed(current, "A câmera deste aparelho não aceitou a configuração.")
            }
        }
        try {
            @Suppress("DEPRECATION")
            device.createCaptureSession(listOf(surface, images.surface), callback, handler)
        } catch (e: Exception) {
            failed(current, "Não foi possível abrir a câmera.")
        }
    }

    /** A câmera falhou: desliga e oferece tentar de novo (se ela ainda for a atual, e não uma que já foi fechada). */
    private fun failed(current: Int, text: String) {
        main.post {
            if (current != generation || done) return@post
            stopCamera()
            show(text, "Tentar de novo") { startCamera() }
        }
    }

    private fun stopCamera() {
        started = false
        val oldSession: CameraCaptureSession?
        val oldCamera: CameraDevice?
        val images: ImageReader?
        val surface: Surface?
        val thread: HandlerThread?
        synchronized(lock) {
            generation++
            oldSession = session
            oldCamera = camera
            images = reader
            surface = previewSurface
            thread = worker
            session = null
            camera = null
            reader = null
            previewSurface = null
            worker = null
        }
        try { oldSession?.close() } catch (e: Exception) { }
        try { oldCamera?.close() } catch (e: Exception) { }
        surface?.release()
        // O leitor fecha na linha da câmera, depois da imagem que estiver sendo analisada (fechá-lo antes estragaria a
        // memória dela).
        if (thread == null) images?.close()
        else {
            Handler(thread.looper).post { images?.close() }
            thread.quitSafely()
        }
    }

    /** A câmera de trás (a de fora, num aparelho sem ela) e um tamanho de imagem que a tela e a leitura aceitem. */
    private fun chooseCamera(manager: CameraManager): Triple<String, CameraCharacteristics, Size>? {
        val ids = manager.cameraIdList
        val facing = { id: String -> manager.getCameraCharacteristics(id).get(CameraCharacteristics.LENS_FACING) }
        val id = ids.firstOrNull { facing(it) == CameraMetadata.LENS_FACING_BACK }
            ?: ids.firstOrNull { facing(it) == CameraMetadata.LENS_FACING_EXTERNAL }
            ?: ids.firstOrNull()
            ?: return null
        val characteristics = manager.getCameraCharacteristics(id)
        val map = characteristics.get(CameraCharacteristics.SCALER_STREAM_CONFIGURATION_MAP) ?: return null
        val size = chooseSize(map.getOutputSizes(SurfaceTexture::class.java), map.getOutputSizes(ImageFormat.YUV_420_888)) ?: return null
        return Triple(id, characteristics, size)
    }

    // Por volta de 1280×720: nítido para ler o QR de longe e leve para analisar várias imagens por segundo.
    private fun chooseSize(previewSizes: Array<Size>?, readSizes: Array<Size>?): Size? {
        val both = readSizes.orEmpty().filter { size -> previewSizes.orEmpty().contains(size) }
        val small = both.filter { max(it.width, it.height) <= 1920 && min(it.width, it.height) <= 1080 }.ifEmpty { both }
        return small.minByOrNull { abs(it.width.toLong() * it.height - 1280L * 720) }
    }

    /**
     * A câmera entrega a imagem de pé na posição natural do aparelho (em pé no celular, deitada em muitos tablets), e
     * a área de exibição estica essa imagem até o tamanho dela. Aqui a imagem volta à proporção certa, gira junto com
     * a tela e cobre a tela inteira (o que sobra fica de fora).
     */
    private fun configureTransform() {
        val size = previewSize ?: return
        val viewWidth = preview.width.toFloat()
        val viewHeight = preview.height.toFloat()
        if (viewWidth == 0f || viewHeight == 0f) return
        val screen = when (displayRotation()) {
            Surface.ROTATION_90 -> 90
            Surface.ROTATION_180 -> 180
            Surface.ROTATION_270 -> 270
            else -> 0
        }
        val sideways = sensorOrientation % 180 != 0
        val naturalWidth = (if (sideways) size.height else size.width).toFloat()
        val naturalHeight = (if (sideways) size.width else size.height).toFloat()
        val shownWidth = if (screen % 180 != 0) naturalHeight else naturalWidth
        val shownHeight = if (screen % 180 != 0) naturalWidth else naturalHeight
        val scale = max(viewWidth / shownWidth, viewHeight / shownHeight)
        val centerX = viewWidth / 2
        val centerY = viewHeight / 2
        preview.setTransform(Matrix().apply {
            setScale(naturalWidth / viewWidth, naturalHeight / viewHeight, centerX, centerY)
            postRotate(-screen.toFloat(), centerX, centerY)
            postScale(scale, scale, centerX, centerY)
        })
    }

    @Suppress("DEPRECATION")
    private fun displayRotation(): Int =
        (if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) display else windowManager.defaultDisplay)?.rotation ?: Surface.ROTATION_0

    // ---------- leitura ----------

    /** Roda na linha da câmera. Enquanto uma imagem é analisada, as que chegam são descartadas (fica só a mais nova). */
    private fun decode(source: ImageReader) {
        val image = try {
            source.acquireLatestImage()
        } catch (e: IllegalStateException) {
            null
        } ?: return
        val width: Int
        val height: Int
        val stride: Int
        try {
            val plane = image.planes[0]
            val buffer = plane.buffer
            val count = buffer.remaining()
            if (luminance.size < count) luminance = ByteArray(count)
            buffer.get(luminance, 0, count)
            width = image.width
            height = image.height
            stride = plane.rowStride
        } catch (e: IllegalStateException) {
            return
        } finally {
            image.close()
        }
        val text = try {
            QrReader.read(luminance, width, height, stride)
        } catch (e: RuntimeException) {
            null
        } ?: return
        main.post { found(text) }
    }

    private fun found(text: String) {
        if (done || text == lastText) return
        lastText = text
        handle(text)
    }

    /** O texto lido: link (ou código) de transmissão do servidor do Ponte abre a transmissão; outro QR só avisa. */
    internal fun handle(text: String) {
        if (done) return
        val stream = try {
            StreamLink.parse(text, Store(this).relayServer)
        } catch (e: IllegalArgumentException) {
            warn(e.message ?: "Este link é de outro servidor.")
            return
        }
        if (stream == null) {
            warn("Este QR Code não é de uma transmissão do Ponte.")
            return
        }
        done = true
        stopCamera()
        startActivity(Intent(this, AssistirActivity::class.java).putExtra(AssistirActivity.EXTRA_URL, stream.url))
        finish()
    }

    // O aviso some depois de alguns segundos, e o mesmo QR pode ser lido de novo.
    private fun warn(text: String) {
        show(text)
        main.removeCallbacks(clearWarning)
        main.postDelayed(clearWarning, 4000)
    }

    private val clearWarning = Runnable {
        lastText = null
        if (started) show(instructions)
    }

    private fun show(text: String, label: String? = null, onClick: (() -> Unit)? = null) {
        message.text = text
        action.text = label
        action.visibility = if (label == null) View.GONE else View.VISIBLE
        action.setOnClickListener { onClick?.invoke() }
    }

    /** Escurece a imagem em volta de um quadrado no meio: o lugar para pôr o QR (a leitura vale na imagem toda). */
    private class Mira(context: Context) : View(context) {
        private val shade = Paint().apply { color = 0x88000000.toInt() }
        private val border = Paint(Paint.ANTI_ALIAS_FLAG).apply {
            style = Paint.Style.STROKE
            color = Color.WHITE
            strokeWidth = 3 * context.resources.displayMetrics.density
        }
        private val hole = RectF()
        private val path = Path()
        private var radius = 0f

        override fun onSizeChanged(width: Int, height: Int, oldWidth: Int, oldHeight: Int) {
            val side = min(width, height) * 0.68f
            val top = (height - side) / 2 - height * 0.06f
            hole.set((width - side) / 2, top, (width + side) / 2, top + side)
            radius = side * 0.06f
            path.reset()
            path.fillType = Path.FillType.EVEN_ODD
            path.addRect(0f, 0f, width.toFloat(), height.toFloat(), Path.Direction.CW)
            path.addRoundRect(hole, radius, radius, Path.Direction.CW)
        }

        override fun onDraw(canvas: Canvas) {
            canvas.drawPath(path, shade)
            canvas.drawRoundRect(hole, radius, radius, border)
        }
    }

    companion object {
        private const val PEDIDO_CAMERA = 1
    }
}
