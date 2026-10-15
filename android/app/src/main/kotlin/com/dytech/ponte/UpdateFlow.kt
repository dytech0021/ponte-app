package com.dytech.ponte

import android.app.Activity
import android.app.AlertDialog
import android.app.PendingIntent
import android.content.ActivityNotFoundException
import android.content.Context
import android.content.Intent
import android.content.pm.PackageInstaller
import android.graphics.Color
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.net.Uri
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.provider.Settings
import android.util.TypedValue
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.view.WindowInsets
import android.widget.Button
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.TextView
import android.widget.Toast
import java.io.File
import java.util.concurrent.CopyOnWriteArraySet
import kotlin.concurrent.thread
import ponte.core.AndroidUpdate
import ponte.core.UpdateFeed

/**
 * Atualização do aplicativo pelo servidor de retransmissão. A versão nova é procurada e baixada sem atrapalhar (ao
 * abrir as telas e a cada 15 minutos com o aplicativo aberto); pronta, aparece um aviso no alto de qualquer tela, com
 * "Atualizar". Tocando, o Ponte se instala sozinho (do Android 12 em diante, sem a tela do instalador) e fecha; a versão
 * nova avisa por notificação ("Ponte atualizado, toque para abrir"), porque o Android não deixa um aplicativo se abrir
 * sozinho em segundo plano (testado no Android 15).
 */
class UpdateFlow(private val activity: Activity, private val store: Store) {
    private val banner = UpdateBanner(activity) { installNow() }
    private val handler = Handler(Looper.getMainLooper())
    private val listener: () -> Unit = { handler.post { refreshBanner() } }
    private var resumed = false
    // Voltando da tela do Android que libera a instalação, a atualização segue sozinha.
    private var waitingPermission = false
    private val periodic = object : Runnable {
        override fun run() {
            if (!resumed) return
            checkIfDue()
            handler.postDelayed(this, CHECK_INTERVAL_MS)
        }
    }

    /** Esconde o aviso (na janela flutuante da transmissão não cabe). */
    var bannerAllowed = true
        set(value) {
            field = value
            refreshBanner()
        }

    fun onResume() {
        resumed = true
        State.listeners.add(listener)
        if (waitingPermission && canInstall()) {
            waitingPermission = false
            installNow()
        }
        refreshBanner()
        handler.removeCallbacks(periodic)
        handler.post(periodic)
    }

    /** A resposta ao pedido de notificação feito antes de instalar: a instalação segue, com ou sem a permissão. */
    fun onPermissionResult(requestCode: Int): Boolean {
        if (requestCode != NOTIFICATION_REQUEST) return false
        installNow()
        return true
    }

    fun onPause() {
        resumed = false
        State.listeners.remove(listener)
        handler.removeCallbacks(periodic)
    }

    private fun checkIfDue() {
        if (!automatic || store.relayServer.isBlank()) return
        if (System.currentTimeMillis() - store.lastUpdateCheck < CHECK_INTERVAL_MS) return
        check(manual = false)
    }

    /** Procura uma versão nova e a deixa baixada. [manual] (menu) avisa também quando não há nada ou deu errado. */
    fun check(manual: Boolean) {
        State.ready?.let {
            if (manual) offer(it)
            return
        }
        if (State.busy) {
            if (manual) Toast.makeText(activity, "Procurando atualização...", Toast.LENGTH_SHORT).show()
            return
        }
        State.busy = true
        store.lastUpdateCheck = System.currentTimeMillis()
        val feed = UpdateFeed(store.relayServer, store.relayKey)
        val context = activity.applicationContext
        if (manual) Toast.makeText(activity, "Procurando atualização...", Toast.LENGTH_SHORT).show()
        thread(name = "ponte-atualizacao") {
            val outcome: Result<AndroidUpdate?> = runCatching {
                val update = feed.check(BuildConfig.VERSION_CODE) ?: return@runCatching null
                // A versão nova é baixada sem perguntar; um download já feito (de uma vez anterior) é reaproveitado.
                val apk = UpdateProvider.file(context)
                if (!(apk.isFile && update.matches(apk.readBytes()))) {
                    apk.parentFile?.mkdirs()
                    try { feed.download(update, apk) } catch (e: Exception) { apk.delete(); throw e }
                }
                verify(context, apk, update)?.let { throw IllegalStateException(it) }
                update
            }
            handler.post {
                State.busy = false
                val update = outcome.getOrNull()
                if (update != null) {
                    State.ready = update
                    State.changed()
                }
                if (!manual || activity.isFinishing || activity.isDestroyed) return@post
                when {
                    update != null -> offer(update)
                    outcome.isFailure -> tell("Não foi possível verificar a atualização: ${outcome.exceptionOrNull()?.message ?: "erro desconhecido"}.")
                    else -> {
                        UpdateProvider.file(context).delete()
                        tell("Você já está com a versão mais recente (${BuildConfig.VERSION_NAME}).")
                    }
                }
            }
        }
    }

    private fun offer(update: AndroidUpdate) {
        AlertDialog.Builder(activity)
            .setTitle("Nova versão do Ponte")
            .setMessage("A versão ${update.versionName} está pronta (esta é a ${BuildConfig.VERSION_NAME}). Ao atualizar, o Ponte fecha sozinho para se instalar e avisa quando terminar: é só tocar no aviso para abrir de novo. A lista de computadores e as autorizações continuam.")
            .setPositiveButton("Atualizar") { _, _ -> installNow() }
            .setNegativeButton("Depois", null)
            .show()
    }

    private fun refreshBanner() {
        val update = State.ready
        when {
            !bannerAllowed || update == null || activity.isFinishing -> banner.hide()
            State.installing -> banner.show("Instalando a versão ${update.versionName}... O Ponte vai fechar e avisar quando terminar.", action = false)
            State.dismissed == update.versionCode -> banner.hide()
            else -> banner.show("Nova versão do Ponte: ${update.versionName}", action = true)
        }
    }

    private fun canInstall() = activity.packageManager.canRequestPackageInstalls()

    /** Instala a versão baixada. O Android encerra o Ponte durante a troca; a versão nova avisa por notificação. */
    // A marca de "pedida no aplicativo" é gravada na hora (commit): o Android encerra o Ponte logo em seguida.
    @android.annotation.SuppressLint("ApplySharedPref")
    fun installNow() {
        val update = State.ready ?: return
        if (State.installing) return
        if (!canInstall()) {
            AlertDialog.Builder(activity)
                .setTitle("Permitir a instalação")
                .setMessage("Para se atualizar, o Ponte precisa da permissão para instalar aplicativos. Na tela que vai abrir, ligue \"Permitir desta fonte\" e volte ao Ponte.")
                .setPositiveButton("Abrir") { _, _ ->
                    waitingPermission = true
                    activity.startActivity(Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES, Uri.parse("package:${activity.packageName}")))
                }
                .setNegativeButton("Cancelar", null)
                .show()
            return
        }
        // O Android não deixa o Ponte se abrir sozinho depois de atualizar; ele avisa por notificação ("toque para
        // abrir"), que do Android 13 em diante precisa de permissão: pedida uma vez, aqui, e a instalação segue com a
        // resposta que vier (veja onPermissionResult).
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU && !notificationsAllowed(activity) &&
            !activity.getSharedPreferences("ponte", Context.MODE_PRIVATE).getBoolean(ASKED_NOTIFICATIONS_KEY, false)) {
            activity.getSharedPreferences("ponte", Context.MODE_PRIVATE).edit().putBoolean(ASKED_NOTIFICATIONS_KEY, true).apply()
            activity.requestPermissions(arrayOf(android.Manifest.permission.POST_NOTIFICATIONS), NOTIFICATION_REQUEST)
            return
        }
        State.installing = true
        State.changed()
        val context = activity.applicationContext
        thread(name = "ponte-instalacao") {
            val problem = try {
                val apk = UpdateProvider.file(context)
                verify(context, apk, update) ?: run {
                    // A versão nova abre o Ponte de novo ao terminar (AtualizadoReceiver); gravado já, porque o
                    // Android encerra o aplicativo durante a troca.
                    context.getSharedPreferences("ponte", Context.MODE_PRIVATE).edit()
                        .putLong(REOPEN_KEY, System.currentTimeMillis()).commit()
                    commit(context, apk)
                    null
                }
            } catch (e: Exception) {
                "A instalação não começou: ${e.message ?: e.javaClass.simpleName}."
            }
            if (problem != null) handler.post {
                State.installing = false
                State.changed()
                if (problem.startsWith("A instalação não começou") && !activity.isDestroyed) installWithSystemInstaller()
                else if (!activity.isDestroyed) tell(problem)
            }
        }
    }

    // Se a instalação direta falhar (algum fabricante), sobra o instalador do Android, que pede a confirmação.
    private fun installWithSystemInstaller() {
        val intent = Intent(Intent.ACTION_VIEW)
            .setDataAndType(UpdateProvider.uri(activity), UpdateProvider.APK_TYPE)
            .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
        try {
            activity.startActivity(intent)
        } catch (e: ActivityNotFoundException) {
            tell("Este aparelho não abriu o instalador de aplicativos.")
        }
    }

    private fun tell(message: String) {
        AlertDialog.Builder(activity).setMessage(message).setPositiveButton("Fechar", null).show()
    }

    /** Estado da atualização, um só para o aplicativo inteiro: a versão nova baixada e conferida fica aqui. */
    object State {
        @Volatile var ready: AndroidUpdate? = null
        @Volatile var busy = false
        @Volatile var installing = false
        /** "Depois": o aviso some até aparecer uma versão mais nova (ou o aplicativo abrir de novo). */
        @Volatile var dismissed = 0
        internal val listeners = CopyOnWriteArraySet<() -> Unit>()
        fun changed() = listeners.forEach { it() }

        /** Os testes começam do zero. */
        internal fun reset() {
            ready = null
            busy = false
            installing = false
            dismissed = 0
        }
    }

    companion object {
        private const val CHECK_INTERVAL_MS = 15 * 60 * 1000L
        /** Quando a atualização foi pedida no aplicativo: a versão nova só avisa ("toque para abrir") se foi há pouco. */
        internal const val REOPEN_KEY = "reabrirAposAtualizar"
        private const val ASKED_NOTIFICATIONS_KEY = "pediuNotificacoes"
        private const val NOTIFICATION_REQUEST = 41

        internal fun notificationsAllowed(context: Context): Boolean =
            context.getSystemService(android.app.NotificationManager::class.java).areNotificationsEnabled()

        /** Os testes desligam a consulta automática, para não falar com o servidor de verdade. */
        @Volatile internal var automatic = true

        // O arquivo tem de ser o Ponte, na versão anunciada, assinado com o mesmo certificado do instalado.
        internal fun verify(context: Context, apk: File, update: AndroidUpdate): String? {
            val manager = context.packageManager
            val candidate = manager.getPackageArchiveInfo(apk.path, UpdatePackage.FLAGS)
            val installed = manager.getPackageInfo(context.packageName, UpdatePackage.FLAGS)
            if (candidate == null || UpdatePackage.code(candidate) != update.versionCode.toLong() || !UpdatePackage.compatible(installed, candidate)) {
                apk.delete()
                return "O arquivo recebido não é uma versão mais nova do Ponte assinada com a mesma chave deste aplicativo, e foi descartado."
            }
            return null
        }

        /**
         * Entrega o APK ao Android numa sessão de instalação. Do Android 12 em diante, um aplicativo que se atualiza
         * (e declara UPDATE_PACKAGES_WITHOUT_USER_ACTION) dispensa a confirmação; antes disso, ou se o Android exigir,
         * a resposta da sessão traz a tela de confirmação, que [AtualizacaoActivity] abre.
         */
        private fun commit(context: Context, apk: File) {
            val installer = context.packageManager.packageInstaller
            val params = PackageInstaller.SessionParams(PackageInstaller.SessionParams.MODE_FULL_INSTALL).apply {
                setAppPackageName(context.packageName)
                setSize(apk.length())
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) setRequireUserAction(PackageInstaller.SessionParams.USER_ACTION_NOT_REQUIRED)
            }
            val id = installer.createSession(params)
            try {
                installer.openSession(id).use { session ->
                    apk.inputStream().use { input ->
                        session.openWrite("Ponte.apk", 0, apk.length()).use { output ->
                            input.copyTo(output)
                            session.fsync(output)
                        }
                    }
                    val status = PendingIntent.getActivity(context, id, Intent(context, AtualizacaoActivity::class.java),
                        PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_MUTABLE)
                    session.commit(status.intentSender)
                }
            } catch (e: Exception) {
                installer.abandonSession(id)
                throw e
            }
        }
    }
}

/** O aviso de versão nova no alto da tela: "Atualizar" instala na hora; "Depois" esconde até a próxima versão. */
internal class UpdateBanner(private val activity: Activity, private val onUpdate: () -> Unit) {
    private var card: LinearLayout? = null
    private var text: TextView? = null
    private var update: Button? = null
    private var later: TextView? = null

    private fun dp(value: Int) = (value * activity.resources.displayMetrics.density).toInt()

    private fun create(): LinearLayout {
        val label = TextView(activity).apply {
            setTextColor(Color.WHITE)
            setTextSize(TypedValue.COMPLEX_UNIT_SP, 15f)
            typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL)
        }
        val dismiss = TextView(activity).apply {
            text = "Depois"
            setTextColor(0xCCFFFFFF.toInt())
            setTextSize(TypedValue.COMPLEX_UNIT_SP, 14f)
            gravity = Gravity.CENTER
            minHeight = dp(40)
            setPadding(dp(12), 0, dp(12), 0)
            setOnClickListener {
                UpdateFlow.State.ready?.let { UpdateFlow.State.dismissed = it.versionCode }
                UpdateFlow.State.changed()
            }
        }
        val button = Button(activity, null, 0, R.style.BotaoPrincipal).apply {
            text = "Atualizar"
            gravity = Gravity.CENTER
            minHeight = dp(40)
            typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL)
            setPadding(dp(16), 0, dp(16), 0)
            setOnClickListener { onUpdate() }
        }
        val layout = LinearLayout(activity).apply {
            id = R.id.aviso_atualizacao
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            setPadding(dp(16), dp(8), dp(8), dp(8))
            background = GradientDrawable().apply {
                setColor(0xF2202428.toInt())
                cornerRadius = dp(14).toFloat()
            }
            elevation = dp(6).toFloat()
            addView(label, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
            addView(dismiss, LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT))
            addView(button, LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT))
            // Abaixo da barra de status, mesmo com a tela desenhando por trás dela.
            setOnApplyWindowInsetsListener { view, insets ->
                (view.layoutParams as? FrameLayout.LayoutParams)?.let {
                    it.topMargin = dp(8) + topInset(insets)
                    view.layoutParams = it
                }
                insets
            }
        }
        val params = FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT, Gravity.TOP).apply {
            leftMargin = dp(12)
            rightMargin = dp(12)
            topMargin = dp(8) + (activity.window.decorView.rootWindowInsets?.let { topInset(it) } ?: 0)
        }
        activity.findViewById<FrameLayout>(android.R.id.content).addView(layout, params)
        card = layout
        text = label
        update = button
        later = dismiss
        return layout
    }

    @Suppress("DEPRECATION")
    private fun topInset(insets: WindowInsets): Int =
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) insets.getInsets(WindowInsets.Type.statusBars() or WindowInsets.Type.displayCutout()).top
        else insets.systemWindowInsetTop

    fun show(message: String, action: Boolean) {
        val layout = card ?: create()
        text?.text = message
        update?.visibility = if (action) View.VISIBLE else View.GONE
        later?.visibility = if (action) View.VISIBLE else View.GONE
        layout.visibility = View.VISIBLE
        layout.bringToFront()
    }

    fun hide() {
        card?.visibility = View.GONE
    }
}
