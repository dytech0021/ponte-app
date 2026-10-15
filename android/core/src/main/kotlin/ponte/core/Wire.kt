package ponte.core

import java.io.DataInputStream
import java.io.EOFException
import java.io.InputStream
import java.io.OutputStream

/** Erro com mensagem pronta para mostrar ao usuário. */
class PonteException(message: String) : Exception(message)

/** Tipos de mensagem, os mesmos de Protocol.cs. */
object Msg {
    const val HELLO = 1
    const val CHALLENGE = 2
    const val AUTH_RESPONSE = 3
    const val AUTH_RESULT = 4
    const val FRAME = 10
    const val CURSOR = 11
    const val AUDIO = 12
    const val CURSOR_POS = 13
    const val MOUSE = 20
    const val KEY = 21
    const val TEXT = 22
    const val CLIPBOARD = 30
    const val SETTINGS = 50
    const val NOTICE = 60
    const val BYE = 99
}

/** O que cada lado sabe fazer além do básico, em bits: vai no Hello e volta no AuthResult (Ponte 1.5.0 em diante). */
object Caps {
    /** Grava os arquivos que chegam. O aplicativo não grava, então não declara. */
    const val FILES = 1
    /** Desenha o ponteiro do mouse que o computador envia. */
    const val CURSOR = 2
    /** Toca o som que o computador envia, quando pedido. */
    const val AUDIO = 4
}

/** Ações da mensagem de mouse. */
object MouseAction {
    const val MOVE = 0
    const val LEFT_DOWN = 1
    const val LEFT_UP = 2
    const val RIGHT_DOWN = 3
    const val RIGHT_UP = 4
    const val MIDDLE_DOWN = 5
    const val MIDDLE_UP = 6
    const val WHEEL = 7
}

class Message(val type: Int, val payload: ByteArray)

/**
 * Mensagens sobre o fluxo cifrado: 1 byte de tipo, 4 de tamanho (little-endian) e o conteúdo.
 * Antes da autenticação o tamanho aceito é pequeno, para um desconhecido não conseguir ocupar memória.
 */
class Channel(input: InputStream, private val output: OutputStream) {
    companion object {
        const val MAX_PAYLOAD = 64 * 1024 * 1024
        private val EMPTY = ByteArray(0)
    }

    private val input = DataInputStream(input)
    private val writeLock = Any()

    @Volatile
    var limit = 8 * 1024

    fun send(type: Int, payload: ByteArray = EMPTY) {
        val header = byteArrayOf(
            type.toByte(),
            payload.size.toByte(), (payload.size shr 8).toByte(), (payload.size shr 16).toByte(), (payload.size shr 24).toByte(),
        )
        synchronized(writeLock) {
            output.write(header)
            if (payload.isNotEmpty()) output.write(payload)
            output.flush()
        }
    }

    fun sendJson(type: Int, value: Map<String, Any?>) = send(type, Json.write(value).toByteArray(Charsets.UTF_8))

    /** Próxima mensagem, ou null quando o outro lado fechou a conexão. */
    fun read(): Message? {
        val header = ByteArray(5)
        try {
            input.readFully(header)
        } catch (e: EOFException) {
            return null
        }
        val length = (header[1].toInt() and 0xFF) or ((header[2].toInt() and 0xFF) shl 8) or
            ((header[3].toInt() and 0xFF) shl 16) or ((header[4].toInt() and 0xFF) shl 24)
        if (length < 0 || length > limit) throw PonteException("O outro computador enviou uma mensagem com tamanho inválido.")
        val payload = ByteArray(length)
        try {
            input.readFully(payload)
        } catch (e: EOFException) {
            return null
        }
        return Message(header[0].toInt() and 0xFF, payload)
    }

    fun expectJson(type: Int): Map<String, Any?> {
        val message = read() ?: throw PonteException("O outro computador encerrou a conexão.")
        if (message.type != type) throw PonteException("O outro computador respondeu fora do esperado.")
        return try {
            Json.parseObject(String(message.payload, Charsets.UTF_8))
        } catch (e: IllegalArgumentException) {
            throw PonteException("O outro computador respondeu fora do esperado.")
        }
    }
}

/** Um destino digitado pelo usuário: ID de 9 dígitos (via servidor de retransmissão) ou endereço direto. */
class Target private constructor(
    val isId: Boolean,
    val id: String,
    val host: String,
    val port: Int,
    /** Como o destino é identificado no que o aplicativo guarda: "id:123456789" ou "ip:endereço". */
    val key: String,
    /** Como aparece na tela. */
    val shown: String,
) {
    companion object {
        const val DIRECT_PORT = 7411
        const val RELAY_PORT = 7412

        fun parse(text: String): Target {
            val trimmed = text.trim()
            val digits = trimmed.filter { it.isDigit() }
            if (digits.length == 9 && trimmed.all { it.isDigit() || it == ' ' || it == '-' })
                return Target(true, digits, "", 0, "id:$digits", Crypto.formatId(digits))
            val (host, port) = endpoint(trimmed, DIRECT_PORT)
            return Target(false, "", host, port, "ip:" + trimmed.lowercase(), trimmed)
        }

        /** "host:porta" só quando há exatamente um ':'; senão o texto inteiro é o host. */
        fun endpoint(text: String, defaultPort: Int): Pair<String, Int> {
            val trimmed = text.trim()
            val colon = trimmed.lastIndexOf(':')
            if (colon > 0 && trimmed.indexOf(':') == colon) {
                val port = trimmed.substring(colon + 1).toIntOrNull()
                if (port != null && port in 1..65535) return trimmed.substring(0, colon) to port
            }
            return trimmed to defaultPort
        }
    }
}
