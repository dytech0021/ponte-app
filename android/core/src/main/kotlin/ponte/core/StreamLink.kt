package ponte.core

import java.net.URI
import java.net.URISyntaxException
import java.net.URLDecoder

/** Uma transmissão de tela do Ponte: o servidor e o código que vai no link (https://servidor/ponte/assistir/#código). */
data class StreamLink(val server: String, val token: String) {
    val url: String get() = "https://$server/ponte/assistir/#$token"

    companion object {
        /** O servidor da página e da transmissão: o mesmo da retransmissão, pelo nome (o certificado é do nome, não do IP). */
        fun webHost(relayServer: String): String? {
            if (relayServer.isBlank()) return null
            val host = Target.endpoint(relayServer, Target.RELAY_PORT).first.trim()
            return if (host.contains('.') && host.any { it.isLetter() } && !host.contains(':')) host else null
        }

        private fun isToken(text: String) = text.length == 24 && text.all { it in 'A'..'Z' || it in 'a'..'z' || it in '0'..'9' }

        /**
         * O link colado (ou só o código) vira a transmissão; null quando não é de transmissão. Link de outro servidor
         * lança IllegalArgumentException com a mensagem pronta: o aplicativo só assiste às do servidor dele.
         */
        fun parse(text: String, relayServer: String): StreamLink? {
            val server = webHost(relayServer) ?: return null
            val trimmed = text.trim()
            if (isToken(trimmed) && trimmed.any { it.isLetter() }) return StreamLink(server, trimmed)
            val uri = try { URI(trimmed) } catch (e: URISyntaxException) { return null }
            val host = uri.host ?: return null
            if (uri.scheme != "https" || !(uri.rawPath ?: "").trimEnd('/').endsWith("/ponte/assistir", ignoreCase = true)) return null
            val token = URLDecoder.decode(uri.rawFragment ?: "", "UTF-8").trim()
            if (!isToken(token)) return null
            require(host.equals(server, ignoreCase = true)) { "Este link é de outro servidor ($host); o Ponte só assiste às transmissões de $server." }
            return StreamLink(server, token)
        }
    }
}
