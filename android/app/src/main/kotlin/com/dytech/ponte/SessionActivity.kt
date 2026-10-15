package com.dytech.ponte

import android.app.Activity
import android.app.AlertDialog
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.graphics.Bitmap
import android.media.AudioManager
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.text.InputType
import android.view.View
import android.view.WindowInsets
import android.view.WindowManager
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.PopupMenu
import android.widget.TextView
import java.io.File
import java.io.IOException
import java.util.concurrent.ArrayBlockingQueue
import java.util.concurrent.TimeUnit
import kotlin.concurrent.thread
import ponte.core.ClientConfig
import ponte.core.ConnectCallbacks
import ponte.core.Frame
import ponte.core.PointerShape
import ponte.core.PonteClient
import ponte.core.PonteException
import ponte.core.Session
import ponte.core.SessionListener
import ponte.core.Sound
import ponte.core.Target

/** A sessão: conecta, mostra a tela do computador e oferece os comandos em volta dela. */
class SessionActivity : Activity() {
    private lateinit var store: Store
    // O aviso de versão nova aparece também durante a sessão.
    private lateinit var updates: UpdateFlow
    private lateinit var screen: RemoteView
    private lateinit var status: TextView
    private lateinit var notice: TextView
    private lateinit var bar: LinearLayout
    private lateinit var keysBar: View
    private lateinit var keysRow: LinearLayout
    private val ui = Handler(Looper.getMainLooper())
    // A tela e o teclado falam com esta ponte, que repassa à sessão enquanto ela existir.
    private val output = object : RemoteInput {
        override val acceptsText: Boolean get() = session?.acceptsText == true
        override fun mouse(x: Int, y: Int, action: Int, wheel: Int) { session?.sendMouse(x, y, action, wheel) }
        override fun key(virtualKey: Int, up: Boolean, extended: Boolean) { session?.sendKey(virtualKey, up, extended) }
        override fun text(text: String) { session?.sendText(text) }
    }
    private val keyboard = Keyboard { if (session != null) output else null }
    private val armedButtons = HashMap<Int, TextView>()
    private val player = SoundPlayer()

    @Volatile private var session: Session? = null
    @Volatile private var leaving = false
    @Volatile private var soundOn = false
    private var targetText = ""
    private var targetKey = ""
    private var keyboardOpen = false
    private var barCollapsed = false
    private var fullScreen = false
    private var modeButton: TextView? = null
    private var screenIndex = 0
    private var quality = 60
    private val hideNotice = Runnable { notice.visibility = View.GONE }
    // A miniatura da tela inicial: logo que a imagem aparece e, depois, de minuto em minuto.
    private val keepThumbnail = object : Runnable {
        override fun run() {
            saveThumbnail()
            ui.postDelayed(this, 60_000)
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_session)
        store = Store(this)
        updates = UpdateFlow(this, store)
        targetText = intent.getStringExtra(EXTRA_TARGET) ?: ""
        screen = findViewById(R.id.tela)
        status = findViewById(R.id.estado)
        notice = findViewById(R.id.aviso)
        bar = findViewById(R.id.barra)
        keysBar = findViewById(R.id.teclasRolagem)
        keysRow = findViewById(R.id.teclas)

        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        // Os botões de volume do aparelho regulam o som do computador, e não o toque do telefone.
        volumeControlStream = AudioManager.STREAM_MUSIC
        useWholeScreen()

        screen.keyboard = keyboard
        screen.touchpad = store.touchpad
        keyboard.onArmedChanged = { ui.post { paintArmed() } }
        keyboard.onNotice = { text -> ui.post { showNotice(text) } }
        buildBar()
        buildKeys()
        bar.visibility = View.GONE

        if (targetText.isBlank()) {
            finish()
            return
        }
        status.text = "Conectando a $targetText..."
        thread(name = "ponte-sessao") { connectAndRun() }
    }

    // A tela do computador usa o aparelho inteiro, inclusive a área do entalhe; quando o teclado abre,
    // o conteúdo encolhe para caber acima dele.
    private fun useWholeScreen() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P)
            window.attributes = window.attributes.apply { layoutInDisplayCutoutMode = WindowManager.LayoutParams.LAYOUT_IN_DISPLAY_CUTOUT_MODE_SHORT_EDGES }
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            window.setDecorFitsSystemWindows(false)
            window.insetsController?.let {
                it.hide(WindowInsets.Type.systemBars())
                it.systemBarsBehavior = android.view.WindowInsetsController.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
            }
            findViewById<View>(R.id.raiz).setOnApplyWindowInsetsListener { view, insets ->
                val keyboardInset = insets.getInsets(WindowInsets.Type.ime()).bottom
                val edges = insets.getInsets(WindowInsets.Type.systemBars() or WindowInsets.Type.displayCutout())
                // Na tela cheia a imagem vai até a borda, inclusive na área da câmera; só o teclado empurra.
                if (fullScreen) view.setPadding(0, 0, 0, keyboardInset)
                else view.setPadding(edges.left, edges.top, edges.right, maxOf(keyboardInset, edges.bottom))
                keyboardOpen = insets.isVisible(WindowInsets.Type.ime())
                insets
            }
        }
        // Antes do Android 11 o próprio sistema encolhe a janela (adjustResize, no manifesto) e as barras ficam visíveis.
    }

    // ---------- conexão ----------

    private fun connectAndRun() {
        val target = Target.parse(targetText)
        targetKey = target.key
        val saved = store.findSaved(target.key)
        val config = ClientConfig(store.relayServer, store.relayKey, store.deviceName, "Android", BuildConfig.VERSION_NAME)
        val connected = try {
            PonteClient.connect(config, targetText, DeviceIdentity.get(), store.knownFingerprint(target.key), saved?.autoAccess == true, Prompts(saved?.autoAccess == true))
        } catch (e: PonteException) {
            ui.post { fail(e.message ?: "Não foi possível conectar.") }
            return
        } catch (e: Exception) {
            // O cofre de chaves do aparelho ou a rede falharam de um jeito que o protocolo não prevê.
            ui.post { fail("Não foi possível conectar: ${e.javaClass.simpleName}.") }
            return
        }
        if (leaving) {
            connected.close()
            return
        }
        store.recordConnection(target, connected.hostName, connected.fingerprint, connected.paired)
        session = connected
        ui.post { onConnected(connected, saved?.paired == true, saved?.autoAccess == true) }

        connected.run(object : SessionListener {
            private var first = true

            override fun onFrame(frame: Frame) {
                screen.applyFrame(frame)
                if (first) {
                    first = false
                    ui.post { status.visibility = View.GONE }
                    ui.postDelayed(keepThumbnail, 2000)
                }
            }

            override fun onPointer(shape: PointerShape?) {
                screen.setPointer(shape)
            }

            override fun onPointerPosition(x: Int, y: Int, onScreen: Boolean) {
                screen.setPointerPosition(x, y, onScreen)
            }

            override fun onSound(sound: Sound) {
                if (soundOn) player.play(sound)
            }

            override fun onClipboard(text: String) {
                ui.post {
                    val clipboard = getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
                    clipboard.setPrimaryClip(ClipData.newPlainText("Ponte", text))
                }
            }

            override fun onNotice(text: String) {
                ui.post { showNotice(text) }
            }

            override fun onClosed(reason: String) {
                ui.post { onSessionClosed(reason) }
            }
        })
    }

    /** As perguntas que a conexão pode fazer. Chegam fora da thread da interface e esperam a resposta. */
    private inner class Prompts(private val remember: Boolean) : ConnectCallbacks {
        override fun askPassword(): String? = ask { answer ->
            val field = EditText(this@SessionActivity).apply {
                inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_PASSWORD
                hint = "Senha de acesso"
            }
            val box = LinearLayout(this@SessionActivity).apply {
                val side = (20 * resources.displayMetrics.density).toInt()
                setPadding(side, side / 2, side, 0)
                addView(field, LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT)
            }
            AlertDialog.Builder(this@SessionActivity)
                .setTitle("Senha de $targetText")
                .setMessage(
                    if (remember) "Entrando com a senha, este aparelho fica autorizado lá e as próximas conexões entram direto."
                    else "Sem a senha, você pode pedir que alguém aceite do outro lado."
                )
                .setView(box)
                .setPositiveButton("Conectar") { _, _ -> answer(Answer(field.text.toString())) }
                .setNeutralButton("Pedir permissão") { _, _ ->
                    status.text = "Aguardando alguém aceitar o pedido no computador..."
                    answer(Answer(""))
                }
                .setNegativeButton("Cancelar") { _, _ -> answer(Answer(null)) }
                .setOnCancelListener { answer(Answer(null)) }
                .show()
        }.text

        override fun confirmNewIdentity(): Boolean = ask { answer ->
            AlertDialog.Builder(this@SessionActivity)
                .setTitle("A identidade mudou")
                .setMessage(
                    "Este computador respondeu com uma identidade diferente da última vez. Em conexão por IP isso acontece quando outro " +
                        "computador passou a usar o endereço; por ID, não deveria acontecer, e pode ser outro equipamento respondendo no lugar.\n\nConectar assim mesmo?"
                )
                .setPositiveButton("Conectar") { _, _ -> answer(Answer("sim")) }
                .setNegativeButton("Cancelar") { _, _ -> answer(Answer(null)) }
                .setOnCancelListener { answer(Answer(null)) }
                .show()
        }.text != null

        private fun ask(show: ((Answer) -> Unit) -> Unit): Answer {
            val reply = ArrayBlockingQueue<Answer>(1)
            ui.post {
                if (leaving || isFinishing) reply.offer(Answer(null)) else show { reply.offer(it) }
            }
            // Se a tela for fechada com a pergunta no ar, ninguém vai responder: a conexão desiste sozinha.
            while (true) {
                reply.poll(500, TimeUnit.MILLISECONDS)?.let { return it }
                if (leaving) return Answer(null)
            }
        }
    }

    private class Answer(val text: String?)

    private fun onConnected(connected: Session, wasPaired: Boolean, wanted: Boolean) {
        screenIndex = connected.screens.firstOrNull { it.primary }?.index ?: 0
        screen.output = output
        bar.visibility = View.VISIBLE
        if (store.fullScreen) setFullScreen(true, announce = false)
        status.text = "Conectado a ${connected.hostName}. Aguardando a imagem..."
        soundOn = connected.hasSound && store.sound
        if (soundOn) connected.sendSettings(screenIndex, quality, true)
        when {
            connected.paired && !wasPaired -> showNotice("Este aparelho ficou autorizado lá: as próximas conexões entram direto, sem aprovação.")
            !connected.paired && wanted -> showNotice("Ainda sem autorização para entrar direto. Lá, marque \"Sempre permitir este computador\" no pedido, ou entre uma vez com a senha.")
            !connected.acceptsText -> showNotice("O Ponte desse computador é anterior à 1.3.0: o teclado do celular só envia letras e números. Atualize-o lá.")
        }
    }

    private fun fail(message: String) {
        if (isFinishing || isDestroyed) return
        AlertDialog.Builder(this)
            .setMessage(message)
            .setPositiveButton("Fechar") { _, _ -> finish() }
            .setOnCancelListener { finish() }
            .show()
    }

    private fun onSessionClosed(reason: String) {
        soundOn = false
        player.silence()
        ui.removeCallbacks(keepThumbnail)
        session = null
        screen.output = null
        if (isFinishing || isDestroyed) return
        if (reason.isEmpty() || leaving) finish() else fail(reason)
    }

    private fun leave() {
        leaving = true
        saveThumbnail()
        keyboard.releaseAll()
        session?.close()
        finish()
    }

    private fun saveThumbnail() {
        if (targetKey.isEmpty()) return
        val small = screen.thumbnail(320, 180) ?: return
        val file = store.thumbnailFile(targetKey)
        thread(name = "ponte-miniatura") {
            try {
                file.parentFile?.mkdirs()
                val partial = File(file.path + ".tmp")
                partial.outputStream().use { small.compress(Bitmap.CompressFormat.JPEG, 80, it) }
                if (!partial.renameTo(file)) partial.delete()
            } catch (e: IOException) {
                // Sem a miniatura a lista mostra o desenho de um monitor; a sessão não depende dela.
            }
            small.recycle()
        }
    }

    private fun toggleSound() {
        val current = session ?: return
        soundOn = !soundOn
        store.sound = soundOn
        current.sendSettings(screenIndex, quality, soundOn)
        if (!soundOn) player.silence()
        showNotice(if (soundOn) "Som ligado: você ouve o que o computador está tocando." else "Som desligado.")
    }

    // O gesto de voltar pede confirmação, para a sessão não cair por um toque sem querer. Na tela cheia,
    // ele só traz os comandos de volta.
    @Deprecated("Deprecated in Java")
    override fun onBackPressed() = if (fullScreen) setFullScreen(false) else confirmLeave()

    /** Tela cheia: sem barra de comandos e com a imagem cobrindo o aparelho inteiro. */
    private fun setFullScreen(on: Boolean, announce: Boolean = true) {
        fullScreen = on
        store.fullScreen = on
        screen.fill = on
        bar.visibility = if (on) View.GONE else View.VISIBLE
        if (on) keysBar.visibility = View.GONE
        findViewById<View>(R.id.raiz).requestApplyInsets()
        if (on) showNotice("Tela cheia: arraste para ver o resto da tela do computador. Para voltar aos comandos, use o gesto de voltar.")
        else if (announce) showNotice("Comandos de volta. Para a tela cheia, toque em Tela cheia.")
    }

    private fun confirmLeave() {
        if (session == null) {
            leave()
            return
        }
        AlertDialog.Builder(this)
            .setMessage("Desconectar deste computador?")
            .setPositiveButton("Desconectar") { _, _ -> leave() }
            .setNegativeButton("Continuar", null)
            .show()
    }

    override fun onResume() {
        super.onResume()
        updates.onResume()
    }

    override fun onPause() {
        updates.onPause()
        super.onPause()
    }

    override fun onRequestPermissionsResult(requestCode: Int, permissions: Array<out String>, grantResults: IntArray) {
        if (!updates.onPermissionResult(requestCode)) super.onRequestPermissionsResult(requestCode, permissions, grantResults)
    }

    override fun onDestroy() {
        leaving = true
        soundOn = false
        ui.removeCallbacksAndMessages(null)
        keyboard.releaseAll()
        session?.close()
        player.release()
        super.onDestroy()
    }

    // ---------- barra de comandos ----------

    private fun chip(parent: LinearLayout, text: String, onClick: (TextView) -> Unit): TextView {
        val density = resources.displayMetrics.density
        val view = TextView(this).apply {
            this.text = text
            textSize = 14f
            setTextColor(0xFFFFFFFF.toInt())
            setBackgroundResource(R.drawable.chip)
            gravity = android.view.Gravity.CENTER
            minWidth = (44 * density).toInt()
            minHeight = (40 * density).toInt()
            setPadding((12 * density).toInt(), 0, (12 * density).toInt(), 0)
            isClickable = true
            isFocusable = false
            setOnClickListener { onClick(this) }
        }
        val params = LinearLayout.LayoutParams(LinearLayout.LayoutParams.WRAP_CONTENT, LinearLayout.LayoutParams.WRAP_CONTENT)
        params.marginEnd = (6 * density).toInt()
        parent.addView(view, params)
        return view
    }

    private fun buildBar() {
        bar.removeAllViews()
        if (barCollapsed) {
            chip(bar, "▼") { barCollapsed = false; buildBar() }
            return
        }
        chip(bar, "Teclado") { toggleKeyboard() }
        modeButton = chip(bar, "") { toggleMode() }
        paintMode()
        chip(bar, "Teclas") { toggleKeys() }
        chip(bar, "Tela cheia") { setFullScreen(true) }
        chip(bar, "Mais") { showMore(it) }
        chip(bar, "Sair") { confirmLeave() }
        chip(bar, "▲") { barCollapsed = true; buildBar() }
    }

    private fun paintMode() {
        modeButton?.text = if (screen.touchpad) "Touchpad" else "Toque"
    }

    private fun toggleMode() {
        screen.touchpad = !screen.touchpad
        store.touchpad = screen.touchpad
        paintMode()
        showNotice(
            if (screen.touchpad) "Touchpad: arraste para mover o ponteiro; toque para clicar onde ele está. Para arrastar uma janela, segure até vibrar e arraste."
            else "Toque direto: toque onde quer clicar. Para arrastar uma janela, segure até vibrar e arraste; com a imagem ampliada, arrastar sem segurar move a imagem."
        )
    }

    private fun toggleKeyboard() {
        // Antes do Android 11 não dá para perguntar ao sistema se o teclado está aberto; vale o que foi pedido por último.
        if (keyboardOpen) screen.hideKeyboard() else screen.showKeyboard()
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.R) keyboardOpen = !keyboardOpen
    }

    private fun toggleKeys() {
        keysBar.visibility = if (keysBar.visibility == View.VISIBLE) View.GONE else View.VISIBLE
    }

    private fun buildKeys() {
        keysRow.removeAllViews()
        armedButtons.clear()
        for ((label, vk) in listOf("Ctrl" to Vk.CONTROL, "Alt" to Vk.ALT, "Shift" to Vk.SHIFT, "Win" to Vk.WIN))
            armedButtons[vk] = chip(keysRow, label) { keyboard.toggleArmed(vk) }
        val keys = listOf(
            "Esc" to Vk.ESCAPE, "Tab" to Vk.TAB, "Del" to Vk.DELETE, "←" to Vk.LEFT, "↑" to Vk.UP, "↓" to Vk.DOWN, "→" to Vk.RIGHT,
            "Home" to Vk.HOME, "End" to Vk.END, "PgUp" to Vk.PAGE_UP, "PgDn" to Vk.PAGE_DOWN, "Ins" to Vk.INSERT,
        ) + (1..12).map { "F$it" to Vk.F1 + it - 1 }
        for ((label, vk) in keys) chip(keysRow, label) { keyboard.tap(vk) }
    }

    private fun paintArmed() {
        for ((vk, button) in armedButtons)
            button.setBackgroundResource(if (keyboard.isArmed(vk)) R.drawable.chip_ativo else R.drawable.chip)
    }

    private fun showMore(anchor: View) {
        val current = session ?: return
        val c = 0x43; val v = 0x56; val x = 0x58; val z = 0x5A; val a = 0x41; val d = 0x44; val r = 0x52
        PopupMenu(this, anchor).apply {
            val shortcuts = menu.addSubMenu("Atalhos de teclado")
            fun item(title: String, vararg keys: Int) = shortcuts.add(title).setOnMenuItemClickListener { keyboard.combo(*keys); true }
            item("Copiar (Ctrl+C)", Vk.CONTROL, c)
            item("Colar (Ctrl+V)", Vk.CONTROL, v)
            item("Recortar (Ctrl+X)", Vk.CONTROL, x)
            item("Desfazer (Ctrl+Z)", Vk.CONTROL, z)
            item("Selecionar tudo (Ctrl+A)", Vk.CONTROL, a)
            item("Trocar de janela (Alt+Tab)", Vk.ALT, Vk.TAB)
            item("Fechar janela (Alt+F4)", Vk.ALT, Vk.F4)
            item("Menu Iniciar (Windows)", Vk.WIN)
            item("Área de trabalho (Windows+D)", Vk.WIN, d)
            item("Executar (Windows+R)", Vk.WIN, r)
            item("Gerenciador de Tarefas (Ctrl+Shift+Esc)", Vk.CONTROL, Vk.SHIFT, Vk.ESCAPE)
            item("Print Screen", Vk.PRINT_SCREEN)
            menu.add("Ver a tela inteira").setOnMenuItemClickListener { screen.fitToScreen(); true }
            menu.add("Digitar o texto copiado no celular").setOnMenuItemClickListener { typeClipboard(); true }
            if (current.hasSound) menu.add(if (soundOn) "Desligar o som" else "Ligar o som").setOnMenuItemClickListener { toggleSound(); true }
            menu.add("Qualidade da imagem").setOnMenuItemClickListener { chooseQuality(); true }
            if (current.screens.size > 1) menu.add("Trocar de monitor").setOnMenuItemClickListener { chooseScreen(); true }
            menu.add("Sobre esta conexão").setOnMenuItemClickListener { showConnectionInfo(); true }
        }.show()
    }

    private fun typeClipboard() {
        val clipboard = getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
        val text = clipboard.primaryClip?.takeIf { it.itemCount > 0 }?.getItemAt(0)?.coerceToText(this)?.toString() ?: ""
        if (text.isEmpty()) showNotice("Não há texto copiado no celular.") else keyboard.type(text.take(20_000))
    }

    private fun chooseQuality() {
        val options = listOf("Baixa (mais rápida)" to 35, "Média" to 60, "Alta" to 85)
        AlertDialog.Builder(this)
            .setTitle("Qualidade da imagem")
            .setSingleChoiceItems(options.map { it.first }.toTypedArray(), options.indexOfFirst { it.second == quality }) { dialog, which ->
                quality = options[which].second
                session?.sendSettings(screenIndex, quality, soundOn)
                dialog.dismiss()
            }
            .show()
    }

    private fun chooseScreen() {
        val screens = session?.screens ?: return
        val labels = screens.map { "Monitor ${it.index + 1}  (${it.width} x ${it.height})${if (it.primary) "  principal" else ""}" }
        AlertDialog.Builder(this)
            .setTitle("Monitor")
            .setSingleChoiceItems(labels.toTypedArray(), screens.indexOfFirst { it.index == screenIndex }) { dialog, which ->
                screenIndex = screens[which].index
                session?.sendSettings(screenIndex, quality, soundOn)
                dialog.dismiss()
            }
            .show()
    }

    private fun showConnectionInfo() {
        val current = session ?: return
        val version = current.hostVersion.ifEmpty { "anterior à 1.2.1" }
        AlertDialog.Builder(this)
            .setTitle(current.hostName)
            .setMessage(
                "Endereço: ${current.target.shown}\n" +
                    "Ponte de lá: $version\n" +
                    "Entrada sem aprovação: ${if (current.paired) "autorizada" else "não autorizada"}\n" +
                    "Som: ${if (!current.hasSound) "o computador não transmite" else if (soundOn) "ligado" else "desligado"}\n\n" +
                    "Identidade do computador:\n${current.fingerprint.chunked(4).joinToString(" ")}"
            )
            .setPositiveButton("Fechar", null)
            .show()
    }

    private fun showNotice(text: String) {
        notice.text = text
        notice.visibility = View.VISIBLE
        ui.removeCallbacks(hideNotice)
        ui.postDelayed(hideNotice, 6000)
    }

    companion object {
        const val EXTRA_TARGET = "alvo"
    }
}
