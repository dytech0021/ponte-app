package com.dytech.ponte

import android.app.Activity
import android.app.AlertDialog
import android.content.Intent
import android.media.projection.MediaProjectionManager
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.view.WindowManager
import android.widget.CheckBox
import android.widget.LinearLayout
import android.widget.TextView
import ponte.core.Crypto
import ponte.core.Permission

/**
 * "Fulano quer acessar este celular": aparece sobre o que estiver na tela, até na tela bloqueada, com contagem
 * regressiva. A mesma pergunta fica na notificação; vale a primeira resposta.
 */
class PedidoActivity : Activity() {
    private val main = Handler(Looper.getMainLooper())
    private var dialog: AlertDialog? = null
    private var answered = false

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        if (Build.VERSION.SDK_INT >= 27) {
            setShowWhenLocked(true)
            setTurnScreenOn(true)
        } else {
            @Suppress("DEPRECATION")
            window.addFlags(WindowManager.LayoutParams.FLAG_SHOW_WHEN_LOCKED or WindowManager.LayoutParams.FLAG_TURN_SCREEN_ON)
        }
        show(intent)
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        show(intent)
    }

    private fun show(intent: Intent) {
        val id = intent.getIntExtra(Pedidos.EXTRA_ID, 0)
        val item = Pedidos.get(id)
        if (item == null) {
            finish()
            return
        }
        dialog?.dismiss()
        answered = false
        val request = item.request
        val pad = (20 * resources.displayMetrics.density).toInt()
        val who = request.name.ifEmpty { "Alguém" }
        val detail = TextView(this).apply {
            text = buildString {
                if (request.user.isNotEmpty() && request.user != request.name) append("Usuário: ${request.user}\n")
                if (request.id.isNotEmpty()) append("ID de quem pede: ${Crypto.formatId(request.id)}\n")
                append("\nQuem acessa vê a tela e controla este celular até a sessão terminar.")
            }
            textSize = 15f
        }
        val remember = CheckBox(this).apply {
            text = "Sempre permitir este aparelho"
            visibility = if (request.canRemember) android.view.View.VISIBLE else android.view.View.GONE
        }
        val layout = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(pad, pad / 2, pad, 0)
            addView(detail)
            addView(remember)
        }
        val created = AlertDialog.Builder(this)
            .setTitle("$who quer acessar este celular")
            .setView(layout)
            .setNegativeButton("Recusar") { _, _ -> reply(id, Permission.REFUSE) }
            .setPositiveButton("Permitir") { _, _ -> reply(id, if (remember.isChecked) Permission.ALWAYS else Permission.ONCE) }
            .setOnCancelListener { reply(id, Permission.REFUSE) }
            .create()
        dialog = created
        created.setOnDismissListener { if (!answered) finish() }
        created.show()
        val refuse = created.getButton(AlertDialog.BUTTON_NEGATIVE)
        val tick = object : Runnable {
            override fun run() {
                val left = ((item.expires - System.currentTimeMillis()) / 1000).toInt()
                if (Pedidos.get(id) == null || left <= 0) {
                    answered = true
                    created.dismiss()
                    finish()
                    return
                }
                refuse.text = "Recusar ($left)"
                main.postDelayed(this, 500)
            }
        }
        tick.run()
    }

    private fun reply(id: Int, permission: Permission) {
        answered = true
        Pedidos.answer(this, id, permission)
        finish()
    }

    override fun onDestroy() {
        main.removeCallbacksAndMessages(null)
        dialog?.dismiss()
        super.onDestroy()
    }
}

/**
 * Pede ao Android a gravação de tela ("Começar") e entrega a resposta ao serviço de acesso. Transparente: só
 * aparece a janela do próprio Android.
 */
class ProjecaoActivity : Activity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        current = java.lang.ref.WeakReference(this)
        if (Build.VERSION.SDK_INT >= 27) {
            setShowWhenLocked(true)
            setTurnScreenOn(true)
        }
        if (savedInstanceState != null) return
        if (AcessoService.session == null) {
            finish()
            return
        }
        try {
            @Suppress("DEPRECATION")
            startActivityForResult(getSystemService(MediaProjectionManager::class.java).createScreenCaptureIntent(), 1)
        } catch (e: RuntimeException) {
            finish()
        }
    }

    @Deprecated("Activity Result API não é usada neste aplicativo.")
    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        @Suppress("DEPRECATION")
        super.onActivityResult(requestCode, resultCode, data)
        if (resultCode == RESULT_OK && data != null) {
            val intent = Intent(this, AcessoService::class.java)
                .setAction(AcessoService.ACTION_PROJECTION)
                .putExtra(AcessoService.EXTRA_CODE, resultCode)
                .putExtra(AcessoService.EXTRA_DATA, data)
            try {
                startForegroundServiceCompat(intent)
            } catch (ignored: RuntimeException) {
            }
        }
        finish()
    }

    private fun startForegroundServiceCompat(intent: Intent) {
        if (Build.VERSION.SDK_INT >= 26) startForegroundService(intent) else startService(intent)
    }

    companion object {
        @Volatile private var current: java.lang.ref.WeakReference<ProjecaoActivity>? = null

        /** A sessão acabou antes de alguém tocar em "Começar": fecha a pergunta do Android que ficou na tela. */
        fun closeIfOpen() {
            val activity = current?.get() ?: return
            activity.runOnUiThread {
                try { activity.finishActivity(1) } catch (ignored: RuntimeException) { }
                activity.finish()
            }
        }
    }
}
