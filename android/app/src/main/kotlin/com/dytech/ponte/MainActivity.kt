package com.dytech.ponte

import android.app.Activity
import android.app.AlertDialog
import android.content.Context
import android.content.Intent
import android.graphics.BitmapFactory
import android.graphics.drawable.GradientDrawable
import android.net.Uri
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.text.InputType
import android.view.Gravity
import android.view.View
import android.view.inputmethod.EditorInfo
import android.widget.CheckBox
import android.widget.ArrayAdapter
import android.widget.EditText
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.PopupMenu
import android.widget.ScrollView
import android.widget.Spinner
import android.widget.TextView
import android.widget.Toast
import ponte.core.Crypto
import ponte.core.Presence
import ponte.core.StreamLink
import ponte.core.Target
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import kotlin.concurrent.thread

/** Tela inicial: o endereço para conectar e as listas de computadores salvos e recentes. */
class MainActivity : Activity() {
    private lateinit var store: Store
    private lateinit var address: EditText
    private lateinit var updates: UpdateFlow
    private var deviceId = ""

    override fun attachBaseContext(newBase: Context) {
        super.attachBaseContext(newBase)
        // O override precisa vir antes do primeiro acesso aos recursos desta Activity.
        Store(newBase).themeMode.overrideConfiguration()?.let { applyOverrideConfiguration(it) }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        setTheme(R.style.Tema)
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)
        store = Store(this)
        updates = UpdateFlow(this, store)
        address = findViewById(R.id.endereco)

        findViewById<View>(R.id.conectar).setOnClickListener { connect(address.text.toString()) }
        findViewById<View>(R.id.salvar).setOnClickListener { editComputer(address.text.toString().trim(), "", true, isNew = true) }
        findViewById<View>(R.id.assistir).setOnClickListener { startActivity(Intent(this, LerQrActivity::class.java)) }
        findViewById<View>(R.id.menu).setOnClickListener { showMenu(it) }
        findViewById<View>(R.id.conta).setOnClickListener { accountClicked() }
        address.setOnEditorActionListener { _, action, _ ->
            if (action == EditorInfo.IME_ACTION_GO) connect(address.text.toString())
            action == EditorInfo.IME_ACTION_GO
        }

        showVersion()
        // A chave do aparelho fica no cofre do Android; criá-la na primeira vez pode levar um instante.
        thread(name = "ponte-identidade") {
            val id = try { Crypto.idFromSpki(DeviceIdentity.get().spki) } catch (e: Exception) { "" }
            runOnUiThread {
                deviceId = id
                showVersion()
            }
        }
    }

    override fun onResume() {
        super.onResume()
        refresh()
        updates.onResume()
        showAccount()
        // Ao voltar para a tela, a lista é conferida com a conta (no máximo uma vez por minuto).
        if (System.currentTimeMillis() - Store.lastAccountSync > 60_000) syncAccount()
        presence.removeCallbacks(checkPresence)
        presence.post(checkPresence)
    }

    override fun onPause() {
        presence.removeCallbacks(checkPresence)
        super.onPause()
    }

    override fun onDestroy() {
        googleLogin?.cancel()
        super.onDestroy()
    }

    private fun showVersion() {
        val id = if (deviceId.isEmpty()) "" else "  ·  este aparelho: ${Crypto.formatId(deviceId)}"
        findViewById<TextView>(R.id.versao).text = "versão ${BuildConfig.VERSION_NAME}$id"
    }

    private fun connect(targetText: String) {
        val target = targetText.trim()
        if (target.isEmpty()) {
            address.requestFocus()
            return
        }
        // Link (ou código) de uma transmissão de tela: assiste aqui dentro, em vez de no navegador.
        val stream = try {
            StreamLink.parse(target, store.relayServer)
        } catch (e: IllegalArgumentException) {
            Toast.makeText(this, e.message, Toast.LENGTH_LONG).show()
            return
        }
        if (stream != null) {
            startActivity(Intent(this, AssistirActivity::class.java).putExtra(AssistirActivity.EXTRA_URL, stream.url))
            return
        }
        startActivity(Intent(this, SessionActivity::class.java).putExtra(SessionActivity.EXTRA_TARGET, target))
    }

    // ---------- listas ----------

    private fun refresh() {
        val saved = store.saved()
        val savedList = findViewById<LinearLayout>(R.id.salvos)
        savedList.removeAllViews()
        findViewById<View>(R.id.salvosVazio).visibility = if (saved.isEmpty()) View.VISIBLE else View.GONE
        for (computer in saved) {
            val state = if (computer.paired) "entra direto" else "pede aprovação"
            val row = addRow(savedList, computer.title, computer.address, state, computer.paired)
            row.tag = computer.key
            showPresence(row)
            row.setOnClickListener { connect(computer.address) }
            row.setOnLongClickListener {
                PopupMenu(this, row, Gravity.END).apply {
                    menu.add("Conectar").setOnMenuItemClickListener { connect(computer.address); true }
                    menu.add("Editar").setOnMenuItemClickListener { editComputer(computer.address, computer.name, computer.autoAccess, isNew = false); true }
                    menu.add("Remover").setOnMenuItemClickListener { confirmRemove(computer); true }
                }.show()
                true
            }
        }

        // Um computador salvo não precisa aparecer de novo entre os recentes.
        val savedKeys = saved.map { it.key }.toSet()
        val recents = store.recents().filter { Target.parse(it.address).key !in savedKeys }
        val recentList = findViewById<LinearLayout>(R.id.recentes)
        recentList.removeAllViews()
        findViewById<View>(R.id.recentesTitulo).visibility = if (recents.isEmpty()) View.GONE else View.VISIBLE
        val dateFormat = SimpleDateFormat("dd/MM HH:mm", Locale.getDefault())
        for (recent in recents) {
            val row = addRow(recentList, recent.name.ifEmpty { recent.address }, recent.address, dateFormat.format(Date(recent.time)), false)
            row.setOnClickListener { connect(recent.address) }
            row.setOnLongClickListener {
                PopupMenu(this, row, Gravity.END).apply {
                    menu.add("Conectar").setOnMenuItemClickListener { connect(recent.address); true }
                    menu.add("Salvar").setOnMenuItemClickListener { editComputer(recent.address, recent.name, true, isNew = true); true }
                    menu.add("Remover").setOnMenuItemClickListener {
                        store.removeRecent(recent.address)
                        refresh()
                        true
                    }
                }.show()
                true
            }
        }
    }

    private fun addRow(parent: LinearLayout, name: String, address: String, state: String, highlight: Boolean): View {
        val row = layoutInflater.inflate(R.layout.item_computador, parent, false)
        row.findViewById<TextView>(R.id.nome).text = name
        row.findViewById<TextView>(R.id.endereco).text = address
        row.findViewById<TextView>(R.id.estado).apply {
            text = state
            setTextColor(getColor(if (highlight) R.color.destaque else R.color.texto_secundario))
        }
        val thumbnail = row.findViewById<ImageView>(R.id.miniatura)
        val file = store.thumbnailFile(Target.parse(address).key)
        if (file.isFile) {
            // As imagens gravadas pela sessão já são pequenas; limita também arquivos antigos.
            val bitmap = try {
                val options = BitmapFactory.Options().apply { inJustDecodeBounds = true }
                BitmapFactory.decodeFile(file.path, options)
                options.inSampleSize = 1
                while (options.outWidth / options.inSampleSize > 640 || options.outHeight / options.inSampleSize > 360) {
                    options.inSampleSize *= 2
                }
                options.inJustDecodeBounds = false
                BitmapFactory.decodeFile(file.path, options)
            } catch (_: RuntimeException) { null }
            if (bitmap != null) {
                thumbnail.setPadding(0, 0, 0, 0)
                thumbnail.setImageBitmap(bitmap)
            }
        }
        parent.addView(row)
        return row
    }

    // ---------- online / offline ----------

    companion object {
        /** De onde vem quem está online. Os testes trocam por uma resposta pronta, para não depender do servidor. */
        internal var presenceSource: (String, String, List<Target>) -> Map<String, Boolean> =
            { server, key, targets -> Presence.check(server, key, targets) }
    }

    private val presence = Handler(Looper.getMainLooper())
    /** Quem estava online na última consulta, por chave. Vale até a próxima, mesmo com a lista refeita. */
    private var online: Map<String, Boolean> = emptyMap()
    @Volatile private var checkingPresence = false

    // Enquanto a tela inicial está à vista: confere agora e de novo a cada 30 s. Fora dela, não gasta rede.
    private val checkPresence: Runnable = object : Runnable {
        override fun run() {
            presence.postDelayed(this, 30_000)
            val targets = store.saved().map { Target.parse(it.address) }
            if (targets.isEmpty() || checkingPresence) return
            checkingPresence = true
            val server = store.relayServer
            val key = store.relayKey
            thread(name = "ponte-online") {
                val seen = try { presenceSource(server, key, targets) } finally { checkingPresence = false }
                runOnUiThread {
                    if (isDestroyed) return@runOnUiThread
                    online = seen
                    val list = findViewById<LinearLayout>(R.id.salvos)
                    for (i in 0 until list.childCount) showPresence(list.getChildAt(i))
                }
            }
        }
    }

    // A bolinha vai logo depois do nome do computador. Verde: o Ponte está aberto lá agora. Vermelho: não está.
    // Sem bolinha: ainda não deu para saber.
    private fun showPresence(row: View) {
        val name = row.findViewById<TextView>(R.id.nome) ?: return
        val on = (row.tag as? String)?.let { online[it] }
        if (on == null) {
            name.setCompoundDrawablesRelative(null, null, null, null)
            name.contentDescription = null
            return
        }
        val size = (16 * resources.displayMetrics.density).toInt()
        val dot = GradientDrawable().apply {
            shape = GradientDrawable.OVAL
            setColor(if (on) 0xFF2EBA5C.toInt() else 0xFFE03E3E.toInt())
            setSize(size, size)
        }
        name.setCompoundDrawablesRelativeWithIntrinsicBounds(null, null, dot, null)
        name.contentDescription = "${name.text}, ${if (on) "online agora" else "offline"}"
    }

    // ---------- salvar, editar, remover ----------

    private fun field(hint: String, value: String, type: Int) = EditText(this).apply {
        this.hint = hint
        setText(value)
        inputType = type
        setSingleLine()
    }

    private fun form(vararg views: View) = ScrollView(this).apply {
        addView(LinearLayout(this@MainActivity).apply {
            orientation = LinearLayout.VERTICAL
            val side = (20 * resources.displayMetrics.density).toInt()
            setPadding(side, side / 2, side, side / 2)
            views.forEach { addView(it, LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT) }
        })
    }

    private fun note(text: String) = TextView(this).apply {
        this.text = text
        textSize = 13f
        setTextColor(getColor(R.color.texto_secundario))
        setPadding(0, (8 * resources.displayMetrics.density).toInt(), 0, 0)
    }

    private fun editComputer(currentAddress: String, currentName: String, auto: Boolean, isNew: Boolean) {
        val name = field("Nome (Escritório, Casa, Loja...)", currentName, InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_FLAG_CAP_WORDS)
        val addressField = field("Endereço (ID ou IP)", currentAddress, InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_VISIBLE_PASSWORD)
        addressField.isEnabled = isNew
        val direct = CheckBox(this).apply {
            text = "Entrar sem aprovação nas próximas vezes"
            isChecked = auto
        }
        val explanation = note(
            "O computador precisa autorizar este aparelho uma vez: alguém marca \"Sempre permitir este computador\" no pedido " +
                "que aparece lá, ou você entra com a senha de acesso dele. Depois disso a conexão entra direto."
        )
        val dialog = AlertDialog.Builder(this)
            .setTitle(if (isNew) "Salvar computador" else "Editar computador")
            .setView(form(name, addressField, direct, explanation))
            .setPositiveButton("Salvar", null)
            .setNegativeButton("Cancelar", null)
            .create()
        dialog.setOnShowListener {
            // O botão só fecha a janela quando o endereço serve.
            dialog.getButton(AlertDialog.BUTTON_POSITIVE).setOnClickListener {
                val typed = addressField.text.toString().trim()
                val onlyDigits = typed.isNotEmpty() && typed.all { it.isDigit() || it == ' ' || it == '-' }
                when {
                    typed.isEmpty() -> addressField.error = "Informe o ID de 9 dígitos ou o IP."
                    onlyDigits && !Target.parse(typed).isId -> addressField.error = "O ID tem 9 dígitos."
                    else -> {
                        store.saveComputer(typed, name.text.toString().trim(), direct.isChecked)
                        dialog.dismiss()
                        refresh()
                        syncAccount()
                    }
                }
            }
        }
        dialog.show()
    }

    private fun confirmRemove(computer: SavedComputer) {
        val note = if (computer.paired) "\n\nA autorização continua valendo no computador até ser removida lá, em \"Computadores autorizados\"." else ""
        AlertDialog.Builder(this)
            .setMessage("Remover ${computer.title} dos computadores salvos?$note")
            .setPositiveButton("Remover") { _, _ ->
                store.removeSaved(computer.key)
                refresh()
                syncAccount()
            }
            .setNegativeButton("Cancelar", null)
            .show()
    }

    // ---------- conta (opcional) ----------

    private var googleLogin: Account.GoogleLogin? = null
    @Volatile private var syncing = false

    private fun showAccount() {
        val account = store.account()
        findViewById<TextView>(R.id.conta).text = if (account == null) "Entrar na conta" else "Conta: ${account.email}"
    }

    private fun accountClicked() {
        val account = store.account() ?: return showSignIn()
        AlertDialog.Builder(this)
            .setTitle("Conta: ${account.email}")
            .setItems(arrayOf("Sincronizar a lista agora", "Criar ou gerenciar a conta no FilaBox", "Sair da conta neste aparelho")) { _, which ->
                when (which) {
                    0 -> syncAccount(manual = true)
                    1 -> openSite("/login")
                    2 -> signOut()
                }
            }
            .setNegativeButton("Fechar", null)
            .show()
    }

    private fun openSite(path: String) {
        try {
            startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(Account.SITE + path)))
        } catch (e: RuntimeException) {
            Toast.makeText(this, "Nenhum navegador para abrir ${Account.SITE + path}", Toast.LENGTH_LONG).show()
        }
    }

    private fun link(text: String, path: String) = note(text).apply {
        setTextColor(getColor(R.color.destaque))
        textSize = 15f
        minHeight = (40 * resources.displayMetrics.density).toInt()
        gravity = Gravity.CENTER_VERTICAL
        setOnClickListener { openSite(path) }
    }

    /** Entrar com a conta do FilaBox: e-mail e senha, ou Google pelo navegador. Entrar é opcional. */
    private fun showSignIn() {
        val email = field("E-mail", "", InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_EMAIL_ADDRESS)
        val password = field("Senha", "", InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_PASSWORD)
        val status = note("")
        val dialog = AlertDialog.Builder(this)
            .setTitle("Entrar com a conta do FilaBox")
            .setView(
                form(
                    note("Os computadores que você salvar ficam na conta e aparecem em qualquer aparelho em que você entrar. " +
                        "Cada conta vê só os próprios. Entrar é opcional."),
                    email, password,
                    link("Criar conta no FilaBox", "/login"),
                    link("Esqueci a senha", "/recuperar-senha"),
                    status,
                )
            )
            .setPositiveButton("Entrar", null)
            .setNeutralButton("Google", null)
            .setNegativeButton("Cancelar", null)
            .create()
        fun busy(text: String, failed: Boolean = false) {
            status.text = text
            status.setTextColor(getColor(if (failed) R.color.perigo else R.color.texto_secundario))
        }
        fun attempt(signIn: () -> Account) = thread(name = "ponte-conta") {
            val outcome = try { Result.success(signIn()) } catch (e: AccountException) { Result.failure(e) }
            runOnUiThread {
                if (isDestroyed) return@runOnUiThread
                dialog.getButton(AlertDialog.BUTTON_POSITIVE)?.isEnabled = true
                dialog.getButton(AlertDialog.BUTTON_NEUTRAL)?.isEnabled = true
                outcome.onSuccess {
                    store.storeAccount(it)
                    dialog.dismiss()
                    showAccount()
                    Toast.makeText(this, "Você entrou como ${it.email}", Toast.LENGTH_SHORT).show()
                    syncAccount(manual = true)
                }.onFailure { busy(it.message ?: "Não foi possível entrar.", failed = true) }
            }
        }
        dialog.setOnShowListener {
            val enter = dialog.getButton(AlertDialog.BUTTON_POSITIVE)
            val google = dialog.getButton(AlertDialog.BUTTON_NEUTRAL)
            enter.setOnClickListener {
                val typed = email.text.toString().trim()
                val secret = password.text.toString()
                if (typed.isEmpty() || secret.isEmpty()) return@setOnClickListener busy("Preencha o e-mail e a senha.", failed = true)
                enter.isEnabled = false
                google.isEnabled = false
                busy("Entrando...")
                attempt { Account.signIn(typed, secret) }
            }
            google.setOnClickListener {
                enter.isEnabled = false
                google.isEnabled = false
                busy("Continue no navegador que abriu. Depois de entrar no Google, volte para cá.")
                googleLogin?.cancel()
                attempt {
                    val login = Account.startGoogle()
                    googleLogin = login
                    runOnUiThread {
                        try {
                            startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(login.url)))
                        } catch (e: RuntimeException) {
                            login.cancel()
                        }
                    }
                    try { login.await() } finally { if (googleLogin === login) googleLogin = null }
                }
            }
        }
        // Fechar a janela no meio do login com Google desiste dele (libera a porta).
        dialog.setOnDismissListener { googleLogin?.cancel() }
        dialog.show()
    }

    /** Confere a lista com a conta, fora da thread da tela. Sem conta, não faz nada. */
    private fun syncAccount(manual: Boolean = false) {
        val account = store.account() ?: return
        if (syncing) return
        syncing = true
        Store.lastAccountSync = System.currentTimeMillis()
        thread(name = "ponte-sincronizar") {
            val outcome = try { Result.success(store.syncAccount(account)) } catch (e: AccountException) { Result.failure(e) }
            syncing = false
            runOnUiThread {
                if (isDestroyed) return@runOnUiThread
                outcome.onSuccess { (added, removed) ->
                    refresh()
                    if (added + removed > 0) Toast.makeText(this, "Lista da conta atualizada: $added chegaram, $removed saíram.", Toast.LENGTH_LONG).show()
                    else if (manual) Toast.makeText(this, "A lista já está igual à da conta.", Toast.LENGTH_SHORT).show()
                }.onFailure {
                    val message = it.message ?: "Falha na conta."
                    // A conta saiu deste aparelho (senha trocada, sessão encerrada em outro lugar): volta ao modo sem conta.
                    if (message.startsWith("A conta saiu")) signOut(message)
                    else if (manual) Toast.makeText(this, "Conta: $message", Toast.LENGTH_LONG).show()
                }
            }
        }
    }

    private fun signOut(reason: String? = null) {
        val account = store.account() ?: return
        store.signOutAccount()
        thread(name = "ponte-sair") { account.signOut() }
        showAccount()
        refresh()
        Toast.makeText(this, reason ?: "Você saiu da conta neste aparelho. Os computadores dela não aparecem mais aqui.", Toast.LENGTH_LONG).show()
    }

    // ---------- menu ----------

    private fun showMenu(anchor: View) {
        PopupMenu(this, anchor).apply {
            menu.add("Verificar atualização").setOnMenuItemClickListener { updates.check(manual = true); true }
            menu.add(0, R.id.configuracoes, 0, "Configurações").setOnMenuItemClickListener { showSettings(); true }
            menu.add("Sobre").setOnMenuItemClickListener { showAbout(); true }
        }.show()
    }

    private fun showSettings() {
        val name = field("Nome deste aparelho", store.deviceName, InputType.TYPE_CLASS_TEXT)
        val server = field("Servidor de retransmissão (endereço:porta)", store.relayServer, InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_URI)
        val key = field("Chave do servidor", store.relayKey, InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_VISIBLE_PASSWORD)
        val themeMode = Spinner(this).apply {
            id = R.id.tema
            contentDescription = "Aparência"
            minimumHeight = (48 * resources.displayMetrics.density).toInt()
            adapter = ArrayAdapter(this@MainActivity, android.R.layout.simple_spinner_item,
                listOf("Automático (sistema)", "Claro", "Escuro")).apply {
                setDropDownViewResource(android.R.layout.simple_spinner_dropdown_item)
            }
            setSelection(store.themeMode.ordinal)
        }
        AlertDialog.Builder(this)
            .setTitle("Configurações")
            .setView(
                form(
                    note("Aparência"), themeMode,
                    note("Nome com que este aparelho aparece no computador"), name,
                    note("Servidor de retransmissão, necessário para conectar por ID"), server,
                    note("Chave do servidor (a mesma do Ponte do computador)"), key,
                )
            )
            .setPositiveButton("Salvar") { _, _ ->
                store.deviceName = name.text.toString()
                store.relayServer = server.text.toString()
                store.relayKey = key.text.toString()
                val selectedTheme = ThemeMode.values()[themeMode.selectedItemPosition]
                if (store.themeMode != selectedTheme) {
                    store.themeMode = selectedTheme
                    recreate()
                }
            }
            .setNegativeButton("Cancelar", null)
            .show()
    }

    private fun showAbout() {
        val id = if (deviceId.isEmpty()) "indisponível" else Crypto.formatId(deviceId)
        AlertDialog.Builder(this)
            .setTitle("Ponte para Android")
            .setMessage(
                "Versão ${BuildConfig.VERSION_NAME}\n\n" +
                    "Endereço deste aparelho: $id\n" +
                    "É com esse número e o nome \"${store.deviceName}\" que ele aparece na lista de computadores autorizados do Ponte no computador.\n\n" +
                    "Este aplicativo acessa computadores que tenham o Ponte aberto. Ele não permite que o celular seja acessado.\n\n" +
                    "O QR Code das transmissões é lido com a biblioteca ZXing (licença Apache 2.0, apache.org/licenses/LICENSE-2.0)."
            )
            .setPositiveButton("Fechar", null)
            .show()
    }
}
