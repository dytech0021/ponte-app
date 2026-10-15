package com.dytech.ponte

import android.content.Context
import android.widget.TextView
import java.net.InetAddress
import java.net.ServerSocket
import java.net.Socket
import java.net.URLDecoder
import kotlin.concurrent.thread
import org.junit.After
import org.junit.Assert.*
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.Robolectric
import org.robolectric.RuntimeEnvironment
import org.robolectric.Shadows.shadowOf
import org.robolectric.annotation.Config
import org.robolectric.shadows.ShadowAlertDialog
import ponte.core.Json
import ponte.core.Target
import ponte.core.str

/** A conta opcional contra um Supabase falso nesta máquina: login, junção das listas, apagar e sair. */
@RunWith(org.robolectric.RobolectricTestRunner::class)
@Config(sdk = [27])
class ContaTest {
    private lateinit var server: ServerSocket
    /** A lista guardada na conta, no "banco", por chave. */
    private val rows = linkedMapOf<String, Map<String, Any?>>()
    private var session = 1

    private val app get() = RuntimeEnvironment.getApplication()
    private val casa = Target.parse("192.168.0.10").key
    private val escritorio = Target.parse("111 222 333").key

    @Before fun start() {
        TokenVault.plainForTests = true
        server = ServerSocket(0, 8, InetAddress.getByName("127.0.0.1"))
        thread(isDaemon = true, name = "supabase-falso") {
            while (true) {
                val client = try { server.accept() } catch (e: java.io.IOException) { break }
                client.use { serve(it) }
            }
        }
        Account.base = "http://127.0.0.1:${server.localPort}"
    }

    /** Um pedido HTTP por conexão (a resposta sai com "Connection: close"). */
    private fun serve(client: Socket) {
        val input = client.getInputStream().buffered()
        fun line(): String {
            val out = StringBuilder()
            while (true) {
                val c = input.read()
                if (c < 0 || c == '\n'.code) break
                if (c != '\r'.code) out.append(c.toChar())
            }
            return out.toString()
        }
        val (method, target) = line().split(" ").let { it[0] to it[1] }
        val headers = generateSequence { line().takeIf { it.isNotEmpty() } }
            .associate { it.substringBefore(':').trim().lowercase() to it.substringAfter(':').trim() }
        val length = headers["content-length"]?.toInt() ?: 0
        val body = ByteArray(length).also { var read = 0; while (read < length) read += input.read(it, read, length - read) }.toString(Charsets.UTF_8)
        val path = target.substringBefore('?')
        val query = target.substringAfter('?', "")
        val (code, answer) = synchronized(rows) {
            when {
                headers["apikey"] != Account.PUBLIC_KEY -> 401 to """{"message":"sem apikey"}"""
                path == "/auth/v1/token" -> token(query, Json.parseObject(body))
                headers["authorization"] != "Bearer acesso$session" -> 401 to """{"message":"JWT expired"}"""
                path == "/auth/v1/logout" -> 204 to ""
                path != "/rest/v1/ponte_computadores" -> 404 to "{}"
                method == "GET" -> 200 to Json.write(rows.values.toList())
                method == "POST" -> {
                    if (!query.contains("on_conflict=user_id,chave")) 400 to "{}" else {
                        @Suppress("UNCHECKED_CAST")
                        (Json.parse(body) as List<Map<String, Any?>>).forEach {
                            if (it.str("user_id") == "u1") rows[it.str("chave")] = it.filterKeys { k -> k != "user_id" }
                        }
                        201 to ""
                    }
                }
                method == "DELETE" -> {
                    rows.remove(URLDecoder.decode(query.substringAfter("chave=eq."), "UTF-8"))
                    204 to ""
                }
                else -> 405 to "{}"
            }
        }
        val bytes = answer.toByteArray()
        client.getOutputStream().apply {
            write("HTTP/1.1 $code X\r\nContent-Type: application/json\r\nContent-Length: ${bytes.size}\r\nConnection: close\r\n\r\n".toByteArray())
            write(bytes)
            flush()
        }
    }

    private fun token(query: String, body: Map<String, Any?>): Pair<Int, String> {
        val ok = when {
            query == "grant_type=password" -> body.str("email") == "pessoa@exemplo.com" && body.str("password") == "certa"
            query == "grant_type=refresh_token" -> (body.str("refresh_token") == "renova$session").also { if (it) session++ }
            else -> false
        }
        if (!ok) {
            return 400 to if (query == "grant_type=password") """{"error_code":"invalid_credentials","msg":"Invalid login credentials"}"""
            else """{"error_code":"refresh_token_not_found","msg":"Invalid Refresh Token: Refresh Token Not Found"}"""
        }
        return 200 to Json.write(mapOf("access_token" to "acesso$session", "refresh_token" to "renova$session", "expires_in" to 3600,
            "user" to mapOf("id" to "u1", "email" to "pessoa@exemplo.com")))
    }

    @After fun stop() {
        Store(app).signOutAccount()
        server.close()
        Account.base = Account.URL_BASE
        TokenVault.plainForTests = false
    }

    private fun row(key: String, address: String, name: String, auto: Boolean) =
        mapOf("chave" to key, "endereco" to address, "nome" to name, "auto_acesso" to auto)

    @Test fun senhaErradaViraMensagemEmPortugues() {
        val error = assertThrows(AccountException::class.java) { Account.signIn("pessoa@exemplo.com", "errada") }
        assertEquals("E-mail ou senha incorretos.", error.message)
    }

    @Test fun juntaAsListasNosDoisSentidosEApagaOQueSaiu() {
        val store = Store(app)
        store.saveComputer("192.168.0.10", "Casa", true)
        rows[escritorio] = row(escritorio, "111 222 333", "Escritório", false)

        val account = Account.signIn(" pessoa@exemplo.com ", "certa")
        assertEquals("pessoa@exemplo.com", account.email)
        store.storeAccount(account)
        // O da conta chegou aqui; o daqui foi para a conta.
        assertEquals(1 to 0, store.syncAccount(account))
        assertEquals(listOf("Casa", "Escritório"), store.saved().map { it.name })
        assertTrue(store.saved().all { it.synced && !it.dirty })
        assertEquals(row(casa, "192.168.0.10", "Casa", true), rows[casa])

        // Mudou aqui e mudou lá ao mesmo tempo: cada lado leva a própria mudança.
        store.saveComputer("192.168.0.10", "Casa nova", false)
        rows[escritorio] = row(escritorio, "111 222 333", "Escritório 2", true)
        assertEquals(0 to 0, store.syncAccount(account))
        assertEquals("Casa nova", rows[casa]!!.str("nome"))
        assertEquals(false, rows[casa]!!["auto_acesso"])
        assertEquals("Escritório 2", store.findSaved(escritorio)!!.name)
        assertTrue(store.findSaved(escritorio)!!.autoAccess)

        // Apagado aqui sai da conta; apagado em outro aparelho sai daqui.
        store.removeSaved(escritorio)
        rows.remove(casa)
        assertEquals(0 to 1, store.syncAccount(account))
        assertTrue(rows.isEmpty())
        assertTrue(store.saved().isEmpty())
    }

    @Test fun apagarSemInternetEsperaAProximaVez() {
        val store = Store(app)
        val account = Account.signIn("pessoa@exemplo.com", "certa")
        store.storeAccount(account)
        store.saveComputer("111 222 333", "Escritório", false)
        store.syncAccount(account)
        assertTrue(escritorio in rows)

        store.removeSaved(escritorio)
        Account.base = "http://127.0.0.1:1"
        val error = assertThrows(AccountException::class.java) { store.syncAccount(account) }
        assertEquals("Sem conexão com o servidor da conta.", error.message)
        assertTrue(escritorio in rows)

        Account.base = "http://127.0.0.1:${server.localPort}"
        assertEquals(0 to 0, store.syncAccount(account))
        assertFalse(escritorio in rows)
        assertTrue(store.saved().isEmpty())
    }

    @Test fun renovaOTokenGuardaCifradoESairDeixaSoOQueEraDaqui() {
        val store = Store(app)
        val prefs = app.getSharedPreferences("ponte", Context.MODE_PRIVATE)
        store.storeAccount(Account.restore("pessoa@exemplo.com", "u1", "renova1")!!)
        assertEquals("renova1", prefs.getString("contaToken", null))

        // Na primeira chamada o token é renovado, e o novo é gravado na hora.
        rows[escritorio] = row(escritorio, "111 222 333", "Escritório", false)
        store.saveComputer("192.168.0.20", "Só daqui", false)
        val account = store.account()!!
        assertEquals(1 to 0, store.syncAccount(account))
        assertEquals("renova2", prefs.getString("contaToken", null))

        store.saveComputer("192.168.0.30", "Salvo depois", false)
        store.signOutAccount()
        assertNull(store.account())
        assertNull(prefs.getString("contaToken", null))
        // Sair leva embora o que veio da conta ou foi para ela; o salvo depois, que não chegou a ir, fica.
        assertEquals(listOf("Salvo depois"), store.saved().map { it.name })

        // Senha trocada ou sessão encerrada em outro lugar: o token antigo não serve mais.
        val error = assertThrows(AccountException::class.java) { Account.restore("pessoa@exemplo.com", "u1", "renova1")!!.list() }
        assertTrue(error.message!!.startsWith("A conta saiu"))
    }

    @Test fun entrarNaContaEOpcionalEFicaNaTelaInicial() {
        UpdateFlow.automatic = false
        DeviceIdentity.replacement = Apoio.identidade()
        try {
            val controller = Robolectric.buildActivity(MainActivity::class.java).setup()
            val activity = controller.get()
            val link = activity.findViewById<TextView>(R.id.conta)
            assertEquals("Entrar na conta", link.text.toString())
            link.performClick()
            val dialog = ShadowAlertDialog.getLatestAlertDialog()
            assertTrue(dialog.isShowing)
            assertEquals("Entrar com a conta do FilaBox", shadowOf(dialog).title.toString())
            dialog.dismiss()
            controller.pause().stop().destroy()
        } finally { DeviceIdentity.replacement = null }
    }
}
