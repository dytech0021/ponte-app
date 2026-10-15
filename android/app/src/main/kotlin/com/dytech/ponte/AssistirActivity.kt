package com.dytech.ponte

import android.annotation.SuppressLint
import android.app.Activity
import android.app.PictureInPictureParams
import android.content.Intent
import android.content.pm.PackageManager
import android.content.res.Configuration
import android.graphics.Color
import android.graphics.Rect
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.util.Rational
import android.view.View
import android.view.ViewGroup
import android.view.WindowInsets
import android.view.WindowInsetsController
import android.view.WindowManager
import android.webkit.JavascriptInterface
import android.webkit.PermissionRequest
import android.webkit.WebChromeClient
import android.webkit.WebResourceRequest
import android.webkit.WebView
import android.webkit.WebViewClient
import android.widget.FrameLayout
import android.widget.Toast
import ponte.core.StreamLink
import kotlin.math.min

/**
 * Assistir a uma transmissão de tela dentro do aplicativo, em vez de no navegador. A transmissão é a mesma: a página
 * de assistir do servidor do Ponte, que toca o vídeo por WebRTC, roda aqui numa tela do próprio aplicativo, com o som
 * ligado e a tela acesa. Quem assiste não controla nada.
 *
 * A tela pode ficar sem as barras do Android (tela cheia) ou com elas, e virar uma janela flutuante por cima dos
 * outros aplicativos (picture-in-picture): pelo botão da página ou saindo do aplicativo com a transmissão aberta.
 */
class AssistirActivity : Activity() {
    private var web: WebView? = null
    private lateinit var container: FrameLayout
    private lateinit var store: Store
    // O aviso de versão nova aparece também assistindo (menos na janela flutuante).
    private lateinit var updates: UpdateFlow
    private var currentUrl = ""
    @Volatile private var fullScreen = true
    private var pipSupported = false
    private var leftPip = false
    private var stopped = false
    private var videoWidth = 16
    private var videoHeight = 9

    /**
     * A transmissão pedida: pelo próprio aplicativo (EXTRA_URL), pelo link lido na câmera ou tocado numa conversa
     * (https://servidor/ponte/assistir/#código) ou pelo botão da página (ponte://assistir/código). Esta tela está aberta
     * a outros aplicativos, então só vale a página de assistir do servidor configurado no Ponte.
     */
    private fun requested(intent: Intent): StreamLink? {
        val relay = Store(this).relayServer
        val data = intent.data
        val text = intent.getStringExtra(EXTRA_URL)
            ?: when (data?.scheme) {
                "https" -> data.toString()
                "ponte" -> data.pathSegments.firstOrNull().takeIf { data.host == "assistir" }
                else -> null
            }
            ?: return null
        return try {
            StreamLink.parse(text, relay)
        } catch (e: IllegalArgumentException) {
            Toast.makeText(this, e.message, Toast.LENGTH_LONG).show()
            null
        }
    }

    // A página que abre aqui é só a de assistir, do servidor do Ponte; qualquer outro endereço vai para o navegador.
    @SuppressLint("SetJavaScriptEnabled", "AddJavascriptInterface")
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val stream = requested(intent)
        if (stream == null) {
            finish()
            return
        }
        store = Store(this)
        updates = UpdateFlow(this, store)
        fullScreen = store.watchFullScreen
        pipSupported = packageManager.hasSystemFeature(PackageManager.FEATURE_PICTURE_IN_PICTURE)
        val page = Uri.parse(stream.url)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        val view = WebView(this).apply {
            id = R.id.transmissao
            setBackgroundColor(Color.BLACK)
            settings.javaScriptEnabled = true
            settings.domStorageEnabled = true
            // O som começa ligado: aqui não é preciso tocar na tela antes, como no navegador.
            settings.mediaPlaybackRequiresUserGesture = false
            settings.userAgentString = "${settings.userAgentString} PonteApp/${BuildConfig.VERSION_NAME}"
            webViewClient = object : WebViewClient() {
                override fun shouldOverrideUrlLoading(view: WebView, request: WebResourceRequest): Boolean {
                    val target = request.url
                    if (target.host == page.host && target.path?.startsWith("/ponte/assistir") == true) return false
                    try { startActivity(Intent(Intent.ACTION_VIEW, target)) } catch (e: RuntimeException) { }
                    return true
                }
            }
            // Assistir não usa câmera nem microfone.
            webChromeClient = object : WebChromeClient() {
                override fun onPermissionRequest(request: PermissionRequest) = request.deny()
            }
            addJavascriptInterface(Bridge(), "PonteApp")
            // Girar o aparelho ou mostrar as barras muda onde o vídeo está: a janela flutuante acompanha.
            addOnLayoutChangeListener { _, left, top, right, bottom, oldLeft, oldTop, oldRight, oldBottom ->
                if (left != oldLeft || top != oldTop || right != oldRight || bottom != oldBottom) updatePip()
            }
        }
        web = view
        container = FrameLayout(this).apply {
            setBackgroundColor(Color.BLACK)
            addView(view, FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT))
            // Fora da tela cheia, a imagem fica entre as barras do Android.
            setOnApplyWindowInsetsListener { target, insets ->
                if (fullScreen || inPip()) target.setPadding(0, 0, 0, 0)
                else if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
                    val bars = insets.getInsets(WindowInsets.Type.systemBars() or WindowInsets.Type.displayCutout())
                    target.setPadding(bars.left, bars.top, bars.right, bars.bottom)
                } else {
                    @Suppress("DEPRECATION")
                    target.setPadding(insets.systemWindowInsetLeft, insets.systemWindowInsetTop, insets.systemWindowInsetRight, insets.systemWindowInsetBottom)
                }
                insets
            }
        }
        setContentView(container)
        drawBehindSystemBars()
        applyFullScreen()
        updatePip()
        currentUrl = stream.url
        view.loadUrl(stream.url)
    }

    // Outro link com a tela já aberta (ela é uma só): troca de transmissão.
    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        val stream = requested(intent) ?: return
        setIntent(intent)
        if (stream.url != currentUrl) {
            currentUrl = stream.url
            web?.loadUrl(stream.url)
        }
    }

    // A tela desenha atrás das barras sempre; quem decide se a imagem passa por baixo delas é applyFullScreen.
    private fun drawBehindSystemBars() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P)
            window.attributes = window.attributes.apply { layoutInDisplayCutoutMode = WindowManager.LayoutParams.LAYOUT_IN_DISPLAY_CUTOUT_MODE_SHORT_EDGES }
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) window.setDecorFitsSystemWindows(false)
    }

    /** Tela cheia: a imagem usa o aparelho inteiro e as barras do sistema aparecem com um deslize na borda. */
    private fun applyFullScreen() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            window.insetsController?.let {
                if (fullScreen) {
                    it.hide(WindowInsets.Type.systemBars())
                    it.systemBarsBehavior = WindowInsetsController.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
                } else {
                    it.show(WindowInsets.Type.systemBars())
                }
            }
        } else {
            @Suppress("DEPRECATION")
            val layout = View.SYSTEM_UI_FLAG_LAYOUT_STABLE or View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN or View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION
            @Suppress("DEPRECATION")
            window.decorView.systemUiVisibility = if (fullScreen)
                layout or View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY or View.SYSTEM_UI_FLAG_FULLSCREEN or View.SYSTEM_UI_FLAG_HIDE_NAVIGATION
            else layout
        }
        container.requestApplyInsets()
    }

    internal fun setFullScreen(on: Boolean) {
        fullScreen = on
        store.watchFullScreen = on
        applyFullScreen()
    }

    // ---------- janela flutuante ----------

    private fun inPip() = isInPictureInPictureMode

    private fun pipParams(): PictureInPictureParams? {
        if (!pipSupported) return null
        // O Android só aceita janelas entre 2,39:1 e 1:2,39.
        val ratio = videoWidth.toDouble() / videoHeight
        val aspect = when {
            ratio > 2.39 -> Rational(239, 100)
            ratio < 1 / 2.39 -> Rational(100, 239)
            else -> Rational(videoWidth, videoHeight)
        }
        return PictureInPictureParams.Builder().setAspectRatio(aspect).apply {
            videoArea()?.let { setSourceRectHint(it) }
            // Do Android 12 em diante, sair do aplicativo com a transmissão aberta já vira janela flutuante.
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
                setAutoEnterEnabled(true)
                setSeamlessResizeEnabled(false)
            }
        }.build()
    }

    // Onde o vídeo aparece na tela (a página o centraliza sem cortar): a janela flutuante sai dali, sem tranco.
    private fun videoArea(): Rect? {
        val view = web ?: return null
        if (view.width == 0 || view.height == 0) return null
        val scale = min(view.width.toFloat() / videoWidth, view.height.toFloat() / videoHeight)
        val width = (videoWidth * scale).toInt()
        val height = (videoHeight * scale).toInt()
        val origin = IntArray(2).also { view.getLocationInWindow(it) }
        val left = origin[0] + (view.width - width) / 2
        val top = origin[1] + (view.height - height) / 2
        return Rect(left, top, left + width, top + height)
    }

    private fun updatePip() {
        val params = pipParams() ?: return
        try { setPictureInPictureParams(params) } catch (e: RuntimeException) { }
    }

    internal fun enterPip(fromButton: Boolean) {
        if (inPip()) return
        val params = pipParams()
        val entered = params != null && try { enterPictureInPictureMode(params) } catch (e: RuntimeException) { false }
        if (!entered && fromButton) {
            Toast.makeText(this,
                if (params == null) "Este aparelho não tem janela flutuante."
                else "A janela flutuante está desligada para o Ponte. Ligue em Configurações > Apps > Ponte > Picture-in-picture.",
                Toast.LENGTH_LONG).show()
        }
    }

    // Antes do Android 12, a janela flutuante ao sair do aplicativo é pedida aqui.
    override fun onUserLeaveHint() {
        super.onUserLeaveHint()
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.S && !isFinishing) enterPip(fromButton = false)
    }

    override fun onPictureInPictureModeChanged(isInPictureInPictureMode: Boolean, newConfig: Configuration) {
        super.onPictureInPictureModeChanged(isInPictureInPictureMode, newConfig)
        // Na janela flutuante a página esconde a barra de botões.
        web?.evaluateJavascript("window.ponteFlutuante && window.ponteFlutuante($isInPictureInPictureMode)", null)
        // Saiu da janela flutuante: ou voltou para a tela (onResume vem em seguida), ou a janela foi fechada, o que
        // encerra a transmissão aqui, como o botão voltar. Conforme a versão do Android, o fechamento chega antes ou
        // depois de onStop.
        updates.bannerAllowed = !isInPictureInPictureMode
        if (isInPictureInPictureMode) {
            container.requestApplyInsets()
        } else if (stopped) {
            finishAndRemoveTask()
        } else {
            leftPip = true
            applyFullScreen()
        }
    }

    /** O que a página de assistir pode pedir ao aplicativo. Só a página do servidor do Ponte abre nesta tela. */
    private inner class Bridge {
        @JavascriptInterface fun telaCheia(): Boolean = fullScreen

        @JavascriptInterface fun definirTelaCheia(on: Boolean) {
            runOnUiThread { if (!isDestroyed) setFullScreen(on) }
        }

        @JavascriptInterface fun podeFlutuar(): Boolean = pipSupported

        @JavascriptInterface fun flutuar() {
            runOnUiThread { if (!isDestroyed) enterPip(fromButton = true) }
        }

        // A janela flutuante fica com a proporção do vídeo.
        @JavascriptInterface fun tamanhoDoVideo(width: Int, height: Int) {
            if (width <= 0 || height <= 0) return
            runOnUiThread {
                if (isDestroyed) return@runOnUiThread
                videoWidth = width
                videoHeight = height
                updatePip()
            }
        }
    }

    // ---------- ciclo de vida ----------

    override fun onStart() {
        super.onStart()
        stopped = false
    }

    override fun onResume() {
        super.onResume()
        leftPip = false
        web?.onResume()
        // Link inválido: a tela fecha já no onCreate, antes de existir o aviso de atualização.
        if (::updates.isInitialized) updates.onResume()
    }

    // Na janela flutuante a tela fica "pausada", mas à vista: o vídeo continua.
    override fun onPause() {
        if (!inPip()) web?.onPause()
        if (::updates.isInitialized) updates.onPause()
        super.onPause()
    }

    override fun onRequestPermissionsResult(requestCode: Int, permissions: Array<out String>, grantResults: IntArray) {
        if (!::updates.isInitialized || !updates.onPermissionResult(requestCode)) super.onRequestPermissionsResult(requestCode, permissions, grantResults)
    }

    override fun onStop() {
        web?.onPause()
        super.onStop()
        stopped = true
        if (leftPip) finishAndRemoveTask()
    }

    override fun onDestroy() {
        web?.destroy()
        web = null
        super.onDestroy()
    }

    companion object {
        const val EXTRA_URL = "url"
    }
}
