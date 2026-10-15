package ponte.core

import java.io.ByteArrayOutputStream
import java.io.EOFException
import java.io.File
import java.net.InetSocketAddress
import java.net.Socket
import java.security.MessageDigest

/** Metadados de transporte. A assinatura do APK, conferida no Android, é a fonte de confiança. */
data class AndroidUpdate(val versionCode: Int, val versionName: String, val size: Long, val sha256: String) {
    companion object {
        const val MAX_SIZE = 64L * 1024 * 1024
        fun parse(text: String, installedCode: Int): AndroidUpdate? {
            val json = Json.parseObject(text)
            val code = json["versionCode"] as? Long ?: errorValue()
            val size = json["size"] as? Long ?: errorValue()
            val name = json["versionName"] as? String ?: errorValue()
            val hash = json["sha256"] as? String ?: errorValue()
            require(code in 1..Int.MAX_VALUE.toLong() && size in 1..MAX_SIZE)
            require(name.matches(Regex("[0-9]{1,5}\\.[0-9]{1,5}\\.[0-9]{1,5}")))
            require(hash.matches(Regex("[a-fA-F0-9]{64}")))
            return if (code > installedCode) AndroidUpdate(code.toInt(), name, size, hash) else null
        }
        private fun errorValue(): Nothing = throw IllegalArgumentException("Manifesto de atualização inválido.")
    }
    fun matches(bytes: ByteArray) = bytes.size.toLong() == size && Crypto.hex(Crypto.sha256(bytes)).equals(sha256, true)
}

class UpdateFeed(private val server: String, private val key: String) {
    fun check(installedCode: Int): AndroidUpdate? {
        val output = ByteArrayOutputStream()
        if (!fetch("ANDROID_MANIFEST", 8192, output)) return null
        return AndroidUpdate.parse(output.toString("UTF-8"), installedCode)
    }

    fun download(update: AndroidUpdate, file: File) {
        try {
            val digest = MessageDigest.getInstance("SHA-256")
            file.outputStream().use { output ->
                java.security.DigestOutputStream(output, digest).use {
                    require(fetch("ANDROID_DOWNLOAD", update.size, it)) { "A atualização não está disponível." }
                }
            }
            require(file.length() == update.size && Crypto.hex(digest.digest()).equals(update.sha256, true)) {
                "O download não confere. Verifique novamente a atualização."
            }
        } catch (e: Exception) { file.delete(); throw e }
    }

    private fun fetch(command: String, limit: Long, output: java.io.OutputStream): Boolean {
        require(server.isNotBlank()) { "Configure o servidor de retransmissão nas configurações." }
        require(key.matches(Regex("[A-Za-z0-9_-]{0,128}"))) { "Chave do servidor inválida." }
        val (host, port) = Target.endpoint(server, Target.RELAY_PORT)
        Socket().use { socket ->
            socket.connect(InetSocketAddress(host, port), 10000)
            socket.soTimeout = 15000
            socket.getOutputStream().write("PONTE $command 0${if (key.isEmpty()) "" else " $key"}\n".toByteArray(Charsets.US_ASCII))
            val input = socket.getInputStream()
            val line = StringBuilder()
            while (true) {
                val b = input.read()
                if (b == -1) throw EOFException("O servidor não oferece atualização para Android ou fechou a conexão.")
                if (b == 10) break
                require(line.length < 256) { "Resposta de atualização inválida." }
                line.append(b.toChar())
            }
            val reply = line.toString().trimEnd('\r')
            if (reply == "ERR none") return false
            val length = if (reply.startsWith("OK ")) reply.substring(3).toLongOrNull() else null
            require(length != null && length in 1..limit) { "Servidor de atualização indisponível ou resposta inválida." }
            var remaining = length
            val buffer = ByteArray(16384)
            val deadline = System.nanoTime() + 90_000_000_000L
            while (remaining > 0) {
                require(System.nanoTime() < deadline) { "O download demorou demais. Tente novamente." }
                val count = input.read(buffer, 0, minOf(buffer.size.toLong(), remaining).toInt())
                if (count < 0) throw EOFException("Download interrompido.")
                output.write(buffer, 0, count)
                remaining -= count
            }
            return true
        }
    }
}
