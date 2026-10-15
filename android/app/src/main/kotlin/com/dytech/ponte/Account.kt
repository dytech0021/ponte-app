package com.dytech.ponte

import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import java.io.IOException
import java.net.HttpURLConnection
import java.net.InetAddress
import java.net.ServerSocket
import java.net.SocketTimeoutException
import java.net.URL
import java.net.URLDecoder
import java.net.URLEncoder
import java.security.KeyStore
import java.security.MessageDigest
import java.security.SecureRandom
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec
import ponte.core.Json
import ponte.core.bool
import ponte.core.int
import ponte.core.str

/** Falha da conta, com a mensagem pronta para mostrar. */
class AccountException(message: String) : Exception(message)

/**
 * Conta opcional, a mesma do FilaBox (login pelo Supabase dele, com Google ou e-mail): guarda a lista de
 * computadores salvos de cada pessoa, para ela aparecer em qualquer aparelho em que a pessoa entrar. Cada conta
 * só enxerga a própria lista (regra do banco). Nada aqui dá acesso a computador nenhum: a permissão de entrar
 * continua em cada computador acessado. Todas as chamadas são bloqueantes: use fora da thread da tela.
 */
class Account private constructor(val email: String, val userId: String, private var refresh: String) {
    private var access = ""
    private var expires = 0L

    /** O token de renovação mudou (o Supabase troca a cada renovação) e precisa ser gravado de novo. */
    var onTokenChanged: ((Account) -> Unit)? = null
    val refreshToken: String get() = refresh

    data class Computer(val key: String, val address: String, val name: String, val autoAccess: Boolean)

    companion object {
        const val URL_BASE = "https://kobtvgqnitdeviddvymm.supabase.co"
        /** Chave pública do Supabase do FilaBox: a mesma que o site entrega a qualquer navegador. */
        const val PUBLIC_KEY = "sb_publishable_S0LPicuJdLg-XsrxGiN0Ug_tIDZ4sLo"
        const val SITE = "https://filabox3d.com.br"
        /** Porta deste aparelho para onde o login com Google volta (a mesma do Ponte do Windows). */
        const val GOOGLE_PORT = 53682
        private const val TABLE = "ponte_computadores"
        /** Só os testes trocam, por um servidor falso nesta máquina. */
        internal var base = URL_BASE

        fun signIn(email: String, password: String): Account =
            fromSession(token("password", mapOf("email" to email.trim(), "password" to password)))

        /** Volta a conta gravada neste aparelho (sem pedir login de novo). */
        fun restore(email: String, userId: String, refresh: String): Account? =
            if (userId.isEmpty() || refresh.isEmpty()) null else Account(email, userId, refresh)

        private fun fromSession(session: Map<String, Any?>): Account {
            @Suppress("UNCHECKED_CAST")
            val user = session["user"] as? Map<String, Any?> ?: throw AccountException("O servidor da conta não informou quem entrou.")
            val account = Account(user.str("email"), user.str("id"), session.str("refresh_token"))
            account.access = session.str("access_token")
            account.expires = System.currentTimeMillis() + maxOf(60, session.int("expires_in", 3600) - 60) * 1000L
            if (account.userId.isEmpty() || account.access.isEmpty()) throw AccountException("O servidor da conta respondeu de um jeito inesperado.")
            return account
        }

        internal fun token(grant: String, body: Map<String, Any?>): Map<String, Any?> {
            val (code, text) = http("POST", "$base/auth/v1/token?grant_type=$grant", null, Json.write(body), emptyMap())
            if (code !in 200..299) throw AccountException(authError(text))
            return try { Json.parseObject(text) } catch (e: IllegalArgumentException) { throw AccountException("O servidor da conta respondeu de um jeito inesperado.") }
        }

        // As mensagens do Supabase vêm em inglês; as comuns viram português.
        private fun authError(body: String): String {
            val json = try { Json.parseObject(body) } catch (e: IllegalArgumentException) { emptyMap() }
            val code = json.str("error_code").ifEmpty { json.str("error") }
            val message = json.str("msg").ifEmpty { json.str("error_description") }
            return when {
                code == "invalid_credentials" || (code == "invalid_grant" && message.contains("credentials", true)) -> "E-mail ou senha incorretos."
                code == "email_not_confirmed" -> "Confirme o e-mail da conta (o link chegou na sua caixa de entrada) e tente de novo."
                code in setOf("refresh_token_not_found", "refresh_token_already_used", "session_not_found") ||
                    (code == "validation_failed" && message.contains("refresh token", true)) -> "A conta saiu deste aparelho. Entre de novo."
                code == "over_request_rate_limit" -> "Muitas tentativas seguidas. Espere um pouco e tente de novo."
                message.isNotEmpty() -> "A conta recusou: ${message.take(200)}"
                else -> "A conta recusou o login."
            }
        }

        internal fun http(method: String, url: String, bearer: String?, body: String?, headers: Map<String, String>): Pair<Int, String> {
            try {
                val connection = URL(url).openConnection() as HttpURLConnection
                connection.requestMethod = method
                connection.connectTimeout = 15_000
                connection.readTimeout = 30_000
                connection.setRequestProperty("apikey", PUBLIC_KEY)
                if (bearer != null) connection.setRequestProperty("Authorization", "Bearer $bearer")
                headers.forEach { (k, v) -> connection.setRequestProperty(k, v) }
                if (body != null) {
                    connection.doOutput = true
                    connection.setRequestProperty("Content-Type", "application/json")
                    connection.outputStream.use { it.write(body.toByteArray()) }
                }
                val code = connection.responseCode
                val stream = if (code in 200..299) connection.inputStream else connection.errorStream
                val text = stream?.use { it.readBytes().toString(Charsets.UTF_8) } ?: ""
                connection.disconnect()
                return code to text
            } catch (e: IOException) {
                throw AccountException("Sem conexão com o servidor da conta.")
            }
        }

        private fun base64Url(data: ByteArray) = Base64.encodeToString(data, Base64.URL_SAFE or Base64.NO_PADDING or Base64.NO_WRAP)

        /**
         * Começo do login com Google: um segredo que fica aqui (PKCE) e a porta deste aparelho para onde o login
         * volta. Abra [url] no navegador e chame [GoogleLogin.await] fora da thread da tela.
         */
        fun startGoogle(): GoogleLogin {
            val verifier = base64Url(ByteArray(32).also { SecureRandom().nextBytes(it) })
            val challenge = base64Url(MessageDigest.getInstance("SHA-256").digest(verifier.toByteArray(Charsets.US_ASCII)))
            val server = try {
                ServerSocket(GOOGLE_PORT, 4, InetAddress.getByName("127.0.0.1"))
            } catch (e: IOException) {
                throw AccountException("A porta $GOOGLE_PORT deste aparelho está ocupada; tente de novo em instantes.")
            }
            val redirect = "http://127.0.0.1:$GOOGLE_PORT/ponte"
            val url = "$base/auth/v1/authorize?provider=google&redirect_to=${URLEncoder.encode(redirect, "UTF-8")}" +
                "&code_challenge=$challenge&code_challenge_method=s256"
            return GoogleLogin(server, verifier, url)
        }
    }

    class GoogleLogin internal constructor(private val server: ServerSocket, private val verifier: String, val url: String) {
        fun cancel() = try { server.close() } catch (e: IOException) {}

        /** Espera o navegador voltar (até 5 minutos) e troca o código pela sessão. */
        fun await(): Account {
            try {
                server.soTimeout = 5 * 60 * 1000
                while (true) {
                    val socket = try {
                        server.accept()
                    } catch (e: SocketTimeoutException) {
                        throw AccountException("O login com Google não voltou para o Ponte a tempo. Tente de novo.")
                    } catch (e: IOException) {
                        throw AccountException("O login com Google foi cancelado.")
                    }
                    val code = socket.use { handle(it) } ?: continue
                    return fromSession(token("pkce", mapOf("auth_code" to code, "code_verifier" to verifier)))
                }
            } finally {
                cancel()
            }
        }

        // Lê "GET /ponte?code=...", responde uma página curta e devolve o código (null para outros pedidos).
        private fun handle(client: java.net.Socket): String? {
            client.soTimeout = 10_000
            val head = try {
                val buffer = ByteArray(8192)
                val read = client.getInputStream().read(buffer)
                if (read <= 0) "" else String(buffer, 0, read, Charsets.US_ASCII)
            } catch (e: IOException) { "" }
            val path = head.substringBefore('\n').trim().split(" ").getOrNull(1) ?: ""
            if (!path.startsWith("/ponte")) {
                respond(client, "")
                return null
            }
            val query = path.substringAfter('?', "").split('&').filter { it.isNotEmpty() }.associate {
                val parts = it.split('=', limit = 2)
                parts[0] to URLDecoder.decode(parts.getOrElse(1) { "" }, "UTF-8")
            }
            val code = query["code"]
            respond(client, if (code != null) "<h2>Pronto!</h2><p>Você entrou na conta. Volte ao aplicativo do Ponte.</p>"
            else "<h2>O login não foi concluído.</h2><p>Volte ao Ponte e tente de novo.</p>")
            return code ?: throw AccountException("O Google não concluiu o login: ${query["error_description"] ?: query["error"] ?: "sem código de retorno"}")
        }

        private fun respond(client: java.net.Socket, html: String) {
            val page = "<!doctype html><html lang=\"pt-BR\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\">" +
                "<title>Ponte</title></head><body style=\"font-family:sans-serif;margin:2em\">$html</body></html>"
            val body = page.toByteArray()
            try {
                client.getOutputStream().apply {
                    write("HTTP/1.1 ${if (html.isEmpty()) 404 else 200} OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: ${body.size}\r\nConnection: close\r\n\r\n".toByteArray())
                    write(body)
                    flush()
                }
            } catch (e: IOException) {}
        }
    }

    // ---------- lista de computadores ----------

    fun list(): List<Computer> {
        val (code, text) = authorized("GET", "/rest/v1/$TABLE?select=chave,endereco,nome,auto_acesso", null)
        check(code, text)
        val rows = try { Json.parse(text) as? List<*> } catch (e: IllegalArgumentException) { null }
            ?: throw AccountException("O servidor da conta respondeu de um jeito inesperado.")
        @Suppress("UNCHECKED_CAST")
        return rows.filterIsInstance<Map<*, *>>().map { it as Map<String, Any?> }
            .map { Computer(it.str("chave"), it.str("endereco"), it.str("nome"), it.bool("auto_acesso")) }
    }

    fun upsert(computers: List<Computer>) {
        if (computers.isEmpty()) return
        val rows = computers.map {
            mapOf("user_id" to userId, "chave" to it.key, "endereco" to it.address, "nome" to it.name, "auto_acesso" to it.autoAccess)
        }
        val (code, text) = authorized("POST", "/rest/v1/$TABLE?on_conflict=user_id,chave", Json.write(rows),
            mapOf("Prefer" to "resolution=merge-duplicates,return=minimal"))
        check(code, text)
    }

    fun delete(key: String) {
        val (code, text) = authorized("DELETE", "/rest/v1/$TABLE?chave=eq.${URLEncoder.encode(key, "UTF-8")}", null)
        check(code, text)
    }

    fun signOut() {
        try { authorized("POST", "/auth/v1/logout?scope=local", null) } catch (e: AccountException) {}
    }

    private fun check(code: Int, text: String) {
        if (code in 200..299) return
        throw AccountException(
            if (code == 404) "A conta ainda não está pronta no servidor (falta a tabela do Ponte)."
            else "O servidor da conta recusou o pedido ($code). ${text.take(200)}"
        )
    }

    @Synchronized
    private fun ensureAccess() {
        if (System.currentTimeMillis() < expires && access.isNotEmpty()) return
        val session = token("refresh_token", mapOf("refresh_token" to refresh))
        access = session.str("access_token")
        refresh = session.str("refresh_token")
        expires = System.currentTimeMillis() + maxOf(60, session.int("expires_in", 3600) - 60) * 1000L
        onTokenChanged?.invoke(this)
    }

    private fun authorized(method: String, path: String, body: String?, headers: Map<String, String> = emptyMap()): Pair<Int, String> {
        ensureAccess()
        return http(method, base + path, access, body, headers)
    }
}

/**
 * Guarda o token de renovação da conta cifrado com uma chave do cofre do Android: o arquivo de preferências
 * sozinho não serve para entrar na conta.
 */
object TokenVault {
    private const val ALIAS = "ponte-conta"

    /** Só para os testes, que rodam fora de um aparelho e não têm o cofre do Android. */
    internal var plainForTests = false

    private fun key(): SecretKey {
        val keyStore = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        (keyStore.getKey(ALIAS, null) as? SecretKey)?.let { return it }
        val generator = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore")
        generator.init(
            KeyGenParameterSpec.Builder(ALIAS, KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .build()
        )
        return generator.generateKey()
    }

    fun seal(text: String): String {
        if (plainForTests) return text
        val cipher = Cipher.getInstance("AES/GCM/NoPadding").apply { init(Cipher.ENCRYPT_MODE, key()) }
        val sealed = cipher.iv + cipher.doFinal(text.toByteArray())
        return Base64.encodeToString(sealed, Base64.NO_WRAP)
    }

    fun open(sealed: String): String? {
        if (sealed.isEmpty()) return null
        if (plainForTests) return sealed
        return try {
            val data = Base64.decode(sealed, Base64.NO_WRAP)
            val cipher = Cipher.getInstance("AES/GCM/NoPadding").apply { init(Cipher.DECRYPT_MODE, key(), GCMParameterSpec(128, data, 0, 12)) }
            String(cipher.doFinal(data, 12, data.size - 12))
        } catch (e: Exception) {
            null
        }
    }
}
