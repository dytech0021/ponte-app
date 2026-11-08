package ponte.core

import java.io.IOException
import java.net.SocketTimeoutException
import java.security.GeneralSecurityException
import java.security.KeyPair
import java.security.KeyPairGenerator
import java.security.Signature
import java.security.spec.ECGenParameterSpec
import java.util.Base64

/** A conta não está na lista de liberadas do dono do Ponte. */
class NotReleasedException(val email: String) : Exception(
    "A conta $email ainda não foi liberada para usar o Ponte. Peça ao dono do Ponte para liberar este e-mail."
)

/**
 * Bilhete da conta (1.17.0), o mesmo do Windows (Ticket.cs): o Ponte só funciona com uma conta liberada pelo dono.
 * O servidor de retransmissão confere a conta e assina "a chave deste aparelho é da conta X, até tal dia". Cada lado
 * de uma sessão manda o seu bilhete e confere o do outro, também na conexão direta por IP.
 */
object Ticket {
    /** Chave pública (SPKI, EC P-256) do servidor que assina os bilhetes. A privada fica só no servidor. */
    const val ISSUER_KEY =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEnhEA2rsYc5UCSKpkORCQ/yTmqKPa33hievj1yunmgcJrtAmWmK++WYiFnXCndxKdaFvr/YMlLEX7wxOou/A/FA=="

    /** Renovar quando faltar menos de uma semana (o bilhete vale 14 dias). */
    const val RENEW_BEFORE_SECONDS = 7L * 24 * 3600

    /** Só os testes: uma chave que também vale, para as sessões de teste não dependerem de uma conta real. */
    @Volatile var testIssuer: KeyPair? = null

    private val issuer = HostCrypto.publicKey(Base64.getDecoder().decode(ISSUER_KEY))!!

    class Check(val email: String?, val reason: String)

    /** Data de vencimento (segundos Unix) sem conferir a assinatura; 0 se o bilhete estiver estragado. */
    fun expires(ticket: String?): Long {
        if (ticket.isNullOrEmpty() || ticket.length > 2048) return 0
        val dot = ticket.indexOf('.')
        if (dot <= 0) return 0
        return try { Json.parseObject(String(decode(ticket.substring(0, dot)), Charsets.UTF_8)).long("x") } catch (e: IllegalArgumentException) { 0 }
    }

    /** Confere o bilhete de um aparelho cuja chave tem o resumo [keyHash]: devolve o e-mail da conta ou o motivo. */
    fun verify(ticket: String?, keyHash: String, now: Long = System.currentTimeMillis() / 1000): Check {
        if (ticket.isNullOrEmpty() || ticket.length > 2048) return Check(null, "sem bilhete da conta")
        val dot = ticket.indexOf('.')
        if (dot <= 0 || dot == ticket.length - 1) return Check(null, "bilhete da conta estragado")
        val payload: ByteArray
        val signature: ByteArray
        try {
            payload = decode(ticket.substring(0, dot))
            signature = decode(ticket.substring(dot + 1))
        } catch (e: IllegalArgumentException) {
            return Check(null, "bilhete da conta estragado")
        }
        val signed = signature.size == 64 && (checks(issuer, payload, signature) || testIssuer?.let { checks(it.public, payload, signature) } == true)
        if (!signed) return Check(null, "bilhete da conta com assinatura inválida")
        val body = try { Json.parseObject(String(payload, Charsets.UTF_8)) } catch (e: IllegalArgumentException) { null }
        if (body == null || body.int("v") != 1 || body.str("e").isEmpty()) return Check(null, "bilhete da conta estragado")
        if (!body.str("k").equals(keyHash, ignoreCase = true)) return Check(null, "bilhete da conta de outro aparelho")
        if (now >= body.long("x")) return Check(null, "bilhete da conta vencido")
        return Check(body.str("e"), "")
    }

    /** O bilhete vale para a chave de resumo [keyHash] por mais [marginSeconds]. */
    fun valid(ticket: String?, keyHash: String, marginSeconds: Long = 0): Boolean =
        verify(ticket, keyHash).email != null && expires(ticket) - System.currentTimeMillis() / 1000 > marginSeconds

    private fun checks(key: java.security.PublicKey, payload: ByteArray, signature: ByteArray): Boolean = try {
        Signature.getInstance("SHA256withECDSA").run {
            initVerify(key)
            update(payload)
            verify(HostCrypto.p1363ToDer(signature))
        }
    } catch (e: GeneralSecurityException) {
        false
    } catch (e: IllegalArgumentException) {
        false
    }

    /**
     * Pede um bilhete ao servidor de retransmissão para a chave de resumo [keyHash], com o token de acesso da conta.
     * Bloqueia: chame fora da thread da interface.
     */
    @Throws(PonteException::class, NotReleasedException::class)
    fun request(relayServer: String, relayKey: String, keyHash: String, accessToken: String): String {
        if (relayServer.isBlank()) throw PonteException("Configure o servidor do Ponte para conferir a conta.")
        val (host, port) = Target.endpoint(relayServer, Target.RELAY_PORT)
        val reply = try {
            PonteHost.open(host, port).use { socket ->
                socket.soTimeout = 25_000
                val key = relayKey.filter { it.code < 128 && it.isLetterOrDigit() || it == '-' || it == '_' }
                val hash = keyHash.lowercase()
                val line = if (key.isNotEmpty()) "PONTE TICKET $hash $key" else "PONTE TICKET $hash"
                PonteHost.writeLine(socket.getOutputStream(), line)
                PonteHost.writeLine(socket.getOutputStream(), accessToken.filter { it.code in 33..126 })
                PonteHost.readLine(socket.getInputStream())
            }
        } catch (e: SocketTimeoutException) {
            throw PonteException("Sem conexão com o servidor do Ponte para conferir a conta.")
        } catch (e: IOException) {
            throw PonteException("Sem conexão com o servidor do Ponte para conferir a conta.")
        }
        if (reply.startsWith("TICKET ") && verify(reply.substring(7), keyHash).email != null) return reply.substring(7)
        if (reply.startsWith("ERR naoliberado")) throw NotReleasedException(reply.substring(minOf(reply.length, 16)).ifEmpty { "desta conta" })
        throw PonteException(
            when {
                reply == "ERR conta" -> "A conta saiu deste aparelho. Entre de novo."
                reply == "ERR servidor" -> "O servidor do Ponte não conseguiu conferir a conta agora. Tente de novo em instantes."
                reply == "ERR key" -> "O servidor de retransmissão recusou a chave de acesso. Confira a chave nas configurações."
                reply.startsWith("TICKET ") -> "O servidor do Ponte mandou um bilhete inválido."
                else -> "O servidor do Ponte ainda não confere contas (falta atualizar o servidor)."
            }
        )
    }

    /** Só os testes: liga a chave de teste e assina um bilhete com ela. */
    fun signForTest(email: String, keyHash: String, expires: Long = System.currentTimeMillis() / 1000 + 86_400): String {
        val pair = testIssuer ?: synchronized(this) {
            testIssuer ?: KeyPairGenerator.getInstance("EC").apply { initialize(ECGenParameterSpec("secp256r1")) }.generateKeyPair().also { testIssuer = it }
        }
        val payload = Json.write(mapOf("v" to 1, "e" to email, "k" to keyHash.uppercase(), "x" to expires)).toByteArray(Charsets.UTF_8)
        return encode(payload) + "." + encode(HostCrypto.sign(pair.private, payload))
    }

    fun encode(data: ByteArray): String = Base64.getUrlEncoder().withoutPadding().encodeToString(data)

    fun decode(text: String): ByteArray = Base64.getUrlDecoder().decode(text)
}
