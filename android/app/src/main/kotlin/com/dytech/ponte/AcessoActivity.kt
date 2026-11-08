package com.dytech.ponte

import android.Manifest
import android.app.Activity
import android.app.AlertDialog
import android.app.NotificationManager
import android.content.ActivityNotFoundException
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.Typeface
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.os.PowerManager
import android.provider.Settings
import android.text.InputType
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.widget.Button
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.Switch
import android.widget.TextView
import android.widget.Toast
import java.text.DateFormat
import java.util.Date
import kotlin.concurrent.thread
import ponte.core.Crypto

/**
 * Acesso a este celular: ligar, o ID, a senha, os aparelhos autorizados e o que falta liberar no Android
 * (controle pela Acessibilidade, notificações e bateria).
 */
class AcessoActivity : Activity() {
    private lateinit var config: AcessoStore
    private lateinit var content: LinearLayout
    private val changed: () -> Unit = { runOnUiThread { if (!isFinishing) render() } }
    private val density get() = resources.displayMetrics.density
    private fun dp(value: Int) = (value * density).toInt()

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        config = AcessoStore(this)
        content = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(20), dp(20), dp(20), dp(28))
        }
        setContentView(ScrollView(this).apply {
            fitsSystemWindows = true
            isFillViewport = true
            addView(content)
        })
    }

    override fun onResume() {
        super.onResume()
        AcessoService.addListener(changed)
        render()
    }

    override fun onPause() {
        AcessoService.removeListener(changed)
        super.onPause()
    }

    // ---------- desenho ----------

    private fun render() {
        content.removeAllViews()
        content.addView(text("Acesso a este celular", 26f, R.color.destaque, bold = true))
        content.addView(text("Deixe um computador, outro celular ou o navegador ver e controlar este celular, como no Ponte do Windows.", 14f, R.color.texto_secundario).apply {
            setPadding(0, dp(4), 0, 0)
        })

        // Ligar e o ID.
        val main = card()
        val toggle = Switch(this).apply {
            text = "Permitir acesso a este celular"
            textSize = 17f
            isChecked = config.enabled
            setOnCheckedChangeListener { _, on -> switchAccess(on) }
        }
        main.addView(toggle)
        if (config.enabled) {
            val id = AcessoService.id ?: try { AcessoIdentity.get(this).id } catch (e: Exception) { null }
            if (id != null) {
                main.addView(text("ID deste celular", 14f, R.color.texto_secundario).apply { setPadding(0, dp(14), 0, 0) })
                val row = LinearLayout(this).apply { gravity = Gravity.CENTER_VERTICAL }
                row.addView(text(Crypto.formatId(id), 28f, R.color.texto, bold = true), LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
                row.addView(button("Copiar", primary = false) {
                    (getSystemService(CLIPBOARD_SERVICE) as ClipboardManager).setPrimaryClip(ClipData.newPlainText("ID", id))
                    Toast.makeText(this, "ID copiado.", Toast.LENGTH_SHORT).show()
                })
                main.addView(row)
            }
            val status = AcessoService.status.ifEmpty { "Ligando..." }
            main.addView(text((if (AcessoService.online) "● " else "○ ") + status, 14f, if (AcessoService.online) R.color.destaque else R.color.texto_secundario).apply {
                setPadding(0, dp(6), 0, 0)
            })
            if (!BilheteConta.hostOk(Store(this))) {
                main.addView(text("Sem uma conta liberada neste celular, ninguém consegue acessá-lo. Entre na conta na tela inicial do Ponte.", 14f, R.color.perigo).apply {
                    setPadding(0, dp(8), 0, 0)
                })
            }
            AcessoService.session?.let { session ->
                main.addView(text("${session.peerName} está acessando agora (${session.access}).", 15f, R.color.texto, bold = true).apply { setPadding(0, dp(12), 0, dp(6)) })
                main.addView(button("Encerrar a sessão", primary = true) { session.stop() })
            }
            main.addView(text("Quem acessa digita este ID no Ponte do computador, no aplicativo de outro celular ou no Ponte do navegador.", 13f, R.color.texto_secundario).apply {
                setPadding(0, dp(10), 0, 0)
            })
        }

        // O que falta liberar.
        section("PARA FUNCIONAR")
        val steps = card()
        val control = ControleService.enabled(this)
        step(
            steps, "Controle pela Acessibilidade", control,
            if (control) "Ligado: quem acessa pode tocar, arrastar, voltar e digitar."
            else "Sem isto, quem acessa só vê a tela. Abra Acessibilidade, toque em Ponte (às vezes dentro de \"Aplicativos instalados\") e ligue. " +
                "Se a opção estiver bloqueada, toque em \"Informações do app\", no menu ⋮ escolha \"Permitir configurações restritas\" e tente de novo.",
            if (control) null else listOf(
                "Abrir Acessibilidade" to { open(Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS)) },
                "Informações do app" to { open(Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, Uri.parse("package:$packageName"))) },
            ),
        )
        val notifications = Build.VERSION.SDK_INT < 33 || checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) == PackageManager.PERMISSION_GRANTED
        step(
            steps, "Notificações", notifications,
            if (notifications) "Os pedidos de acesso aparecem aqui no celular."
            else "Sem notificações, os pedidos de acesso (\"Fulano quer acessar\") podem não aparecer.",
            if (notifications) null else listOf("Permitir" to { requestPermissions(arrayOf(Manifest.permission.POST_NOTIFICATIONS), 1) }),
        )
        if (Build.VERSION.SDK_INT >= 34) {
            val fullScreen = getSystemService(NotificationManager::class.java).canUseFullScreenIntent()
            step(
                steps, "Pedido na tela cheia", fullScreen,
                if (fullScreen) "O pedido de acesso aparece por cima de tudo, até com a tela apagada."
                else "Para o pedido de acesso aparecer por cima de tudo (como uma ligação), libere \"Notificações em tela cheia\".",
                if (fullScreen) null else listOf("Liberar" to { open(Intent(Settings.ACTION_MANAGE_APP_USE_FULL_SCREEN_INTENT, Uri.parse("package:$packageName"))) }),
            )
        }
        val battery = getSystemService(PowerManager::class.java).isIgnoringBatteryOptimizations(packageName)
        step(
            steps, "Bateria sem restrição", battery,
            if (battery) "O Android não desliga o acesso para economizar bateria."
            else "Sem isto, o Android pode desligar o acesso com o celular parado, e ele sai do ar.",
            if (battery) null else listOf("Liberar" to {
                @Suppress("BatteryLife")
                open(Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS, Uri.parse("package:$packageName")))
            }),
        )
        steps.addView(text("A cada sessão o Android pergunta se pode gravar a tela: alguém toca em \"Começar\" no celular. Enquanto isso, a imagem vai mais lenta (Android 11 em diante, com o controle ligado).", 13f, R.color.texto_secundario).apply {
            setPadding(0, dp(12), 0, 0)
        })

        // Senha e autorizados.
        section("SENHA E APARELHOS AUTORIZADOS")
        val access = card()
        access.addView(text("Senha de acesso", 16f, R.color.texto, bold = true))
        access.addView(text(
            if (config.hasPassword) "Definida: quem souber a senha entra sem precisar de alguém aqui."
            else "Sem senha: alguém precisa tocar em Permitir aqui no celular a cada pedido (ou o aparelho já estar autorizado).",
            14f, R.color.texto_secundario,
        ))
        val passwordRow = LinearLayout(this).apply { setPadding(0, dp(10), 0, 0) }
        passwordRow.addView(button(if (config.hasPassword) "Trocar a senha" else "Definir senha", primary = false) { askPassword() })
        if (config.hasPassword) {
            passwordRow.addView(View(this), LinearLayout.LayoutParams(dp(10), 1))
            passwordRow.addView(button("Tirar a senha", primary = false) {
                AlertDialog.Builder(this).setTitle("Tirar a senha?")
                    .setMessage("Depois disso, quem não estiver autorizado só entra se alguém tocar em Permitir aqui.")
                    .setNegativeButton("Cancelar", null)
                    .setPositiveButton("Tirar") { _, _ -> config.setPassword(null); render() }
                    .show()
            })
        }
        access.addView(passwordRow)

        access.addView(text("Aparelhos que entram direto", 16f, R.color.texto, bold = true).apply { setPadding(0, dp(18), 0, 0) })
        val trusted = config.trusted()
        if (trusted.isEmpty()) {
            access.addView(text("Nenhum. Marque \"Sempre permitir\" num pedido, ou entre com a senha pedindo para lembrar.", 14f, R.color.texto_secundario))
        }
        val format = DateFormat.getDateInstance(DateFormat.SHORT)
        for (item in trusted.sortedByDescending { it.lastUsed }) {
            val row = LinearLayout(this).apply { gravity = Gravity.CENTER_VERTICAL; setPadding(0, dp(8), 0, 0) }
            val texts = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
            texts.addView(text(item.name.ifEmpty { "Sem nome" } + if (item.user.isNotEmpty() && item.user != item.name) " (${item.user})" else "", 15f, R.color.texto))
            texts.addView(text(
                (if (item.id.isNotEmpty()) "ID ${Crypto.formatId(item.id)} · " else "") + "último acesso ${format.format(Date(item.lastUsed))}",
                13f, R.color.texto_secundario,
            ))
            row.addView(texts, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
            row.addView(button("Remover", primary = false) { config.removeTrusted(item.keyHash); render() })
            access.addView(row)
        }

        // Opções.
        section("OPÇÕES")
        val options = card()
        options.addView(Switch(this).apply {
            text = "Ligar sozinho quando o celular ligar"
            textSize = 16f
            isChecked = config.startOnBoot
            setOnCheckedChangeListener { _, on -> config.startOnBoot = on }
        })
        options.addView(Switch(this).apply {
            text = "Sempre pronto (o celular não dorme enquanto o acesso estiver ligado; gasta mais bateria)"
            textSize = 16f
            setPadding(0, dp(12), 0, 0)
            isChecked = config.alwaysReady
            setOnCheckedChangeListener { _, on ->
                config.alwaysReady = on
                if (config.enabled) restartService()
            }
        })
        options.addView(text("Encerrar a sessão parada depois de", 16f, R.color.texto).apply { setPadding(0, dp(16), 0, 0) })
        options.addView(button(idleLabel(config.idleMinutes), primary = false) { chooseIdle() }.apply {
            layoutParams = LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply { topMargin = dp(6) }
        })
    }

    private fun step(parent: LinearLayout, title: String, done: Boolean, detail: String, actions: List<Pair<String, () -> Unit>>?) {
        if (parent.childCount > 0) parent.addView(View(this), LinearLayout.LayoutParams(1, dp(14)))
        parent.addView(text((if (done) "✓ " else "✗ ") + title, 16f, if (done) R.color.destaque else R.color.perigo, bold = true))
        parent.addView(text(detail, 14f, R.color.texto_secundario))
        if (actions != null) {
            val row = LinearLayout(this).apply { setPadding(0, dp(8), 0, 0) }
            actions.forEachIndexed { index, (label, action) ->
                if (index > 0) row.addView(View(this), LinearLayout.LayoutParams(dp(10), 1))
                row.addView(button(label, primary = index == 0) { action() })
            }
            parent.addView(row)
        }
    }

    private fun section(title: String) {
        content.addView(TextView(this, null, 0, R.style.TituloDeSecao).apply {
            text = title
            setPadding(0, dp(26), 0, 0)
        })
    }

    private fun card(): LinearLayout {
        val card = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setBackgroundResource(R.drawable.cartao)
            elevation = density
            setPadding(dp(16), dp(16), dp(16), dp(16))
        }
        content.addView(card, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply { topMargin = dp(12) })
        return card
    }

    private fun text(value: String, size: Float, color: Int, bold: Boolean = false) = TextView(this).apply {
        text = value
        textSize = size
        setTextColor(getColor(color))
        if (bold) setTypeface(typeface, Typeface.BOLD)
    }

    private fun button(label: String, primary: Boolean, action: () -> Unit) =
        Button(this, null, 0, if (primary) R.style.BotaoPrincipal else R.style.BotaoSecundario).apply {
            text = label
            gravity = Gravity.CENTER
            setPadding(dp(16), dp(6), dp(16), dp(6))
            setOnClickListener { action() }
        }

    // ---------- ações ----------

    private fun switchAccess(on: Boolean) {
        if (on == config.enabled) return
        config.enabled = on
        if (on) {
            if (Build.VERSION.SDK_INT >= 33 && checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) {
                requestPermissions(arrayOf(Manifest.permission.POST_NOTIFICATIONS), 1)
            }
            AcessoService.start(this)
        } else {
            AcessoService.stop(this)
        }
        render()
    }

    private fun restartService() {
        AcessoService.stop(this)
        content.postDelayed({ if (config.enabled) AcessoService.start(this) }, 600)
    }

    override fun onRequestPermissionsResult(requestCode: Int, permissions: Array<out String>, grantResults: IntArray) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults)
        render()
    }

    private fun open(intent: Intent) {
        try {
            startActivity(intent)
        } catch (e: ActivityNotFoundException) {
            Toast.makeText(this, "Abra pelas Configurações do Android.", Toast.LENGTH_LONG).show()
        } catch (e: SecurityException) {
            Toast.makeText(this, "Abra pelas Configurações do Android.", Toast.LENGTH_LONG).show()
        }
    }

    private fun askPassword() {
        val pad = dp(20)
        val first = EditText(this).apply {
            hint = "Senha nova"
            inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_PASSWORD
        }
        val second = EditText(this).apply {
            hint = "Repita a senha"
            inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_PASSWORD
        }
        val layout = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(pad, pad / 2, pad, 0)
            addView(first)
            addView(second)
            addView(text("Com a senha, entra quem a souber, de qualquer lugar. Use uma senha forte, com pelo menos 8 caracteres.", 13f, R.color.texto_secundario))
        }
        val dialog = AlertDialog.Builder(this)
            .setTitle("Senha de acesso")
            .setView(layout)
            .setNegativeButton("Cancelar", null)
            .setPositiveButton("Salvar", null)
            .create()
        dialog.setOnShowListener {
            dialog.getButton(AlertDialog.BUTTON_POSITIVE).setOnClickListener {
                val password = first.text.toString()
                when {
                    password.length < 8 -> first.error = "Pelo menos 8 caracteres."
                    password != second.text.toString() -> second.error = "As duas não são iguais."
                    else -> {
                        dialog.getButton(AlertDialog.BUTTON_POSITIVE).isEnabled = false
                        // 200 mil rodadas do PBKDF2: um ou dois segundos, fora da tela.
                        thread {
                            config.setPassword(password)
                            runOnUiThread {
                                dialog.dismiss()
                                Toast.makeText(this, "Senha salva.", Toast.LENGTH_SHORT).show()
                                render()
                            }
                        }
                    }
                }
            }
        }
        dialog.show()
    }

    private val idleChoices = intArrayOf(0, 10, 30, 60, 120, 480)

    private fun idleLabel(minutes: Int) = when (minutes) {
        0 -> "Nunca"
        60 -> "1 hora"
        120 -> "2 horas"
        480 -> "8 horas"
        else -> "$minutes minutos"
    }

    private fun chooseIdle() {
        AlertDialog.Builder(this)
            .setTitle("Encerrar a sessão parada depois de")
            .setItems(idleChoices.map { idleLabel(it) }.toTypedArray()) { _, which ->
                config.idleMinutes = idleChoices[which]
                render()
            }
            .show()
    }
}
