package ponte.core

/**
 * JSON mínimo para as mensagens do protocolo e para o que o aplicativo guarda: objetos, listas,
 * textos, números, lógicos e nulo. Fica aqui para o núcleo não depender de biblioteca nenhuma.
 */
object Json {
    fun write(value: Any?): String = StringBuilder().also { write(it, value) }.toString()

    private fun write(out: StringBuilder, value: Any?) {
        when (value) {
            null -> out.append("null")
            is String -> quote(out, value)
            is Boolean, is Int, is Long -> out.append(value.toString())
            is Number -> out.append(value.toDouble().toString())
            is Map<*, *> -> {
                out.append('{')
                var first = true
                for ((k, v) in value) {
                    if (!first) out.append(',')
                    first = false
                    quote(out, k.toString())
                    out.append(':')
                    write(out, v)
                }
                out.append('}')
            }
            is Iterable<*> -> {
                out.append('[')
                var first = true
                for (v in value) {
                    if (!first) out.append(',')
                    first = false
                    write(out, v)
                }
                out.append(']')
            }
            else -> throw IllegalArgumentException("Tipo sem representação em JSON: ${value::class}")
        }
    }

    private fun quote(out: StringBuilder, text: String) {
        out.append('"')
        for (c in text) {
            when {
                c == '"' -> out.append("\\\"")
                c == '\\' -> out.append("\\\\")
                c == '\n' -> out.append("\\n")
                c == '\r' -> out.append("\\r")
                c == '\t' -> out.append("\\t")
                c < ' ' -> out.append("\\u").append(c.code.toString(16).padStart(4, '0'))
                else -> out.append(c)
            }
        }
        out.append('"')
    }

    /** Devolve Map, List, String, Long, Double, Boolean ou null. Lança [IllegalArgumentException] em texto inválido. */
    fun parse(text: String): Any? {
        val parser = Parser(text)
        val value = parser.value()
        parser.skipSpace()
        if (parser.pos != text.length) parser.fail("sobrou texto depois do valor")
        return value
    }

    @Suppress("UNCHECKED_CAST")
    fun parseObject(text: String): Map<String, Any?> =
        parse(text) as? Map<String, Any?> ?: throw IllegalArgumentException("Esperava um objeto JSON.")

    private class Parser(private val s: String) {
        var pos = 0

        fun fail(why: String): Nothing = throw IllegalArgumentException("JSON inválido na posição $pos: $why")

        fun skipSpace() {
            while (pos < s.length && s[pos].isWhitespace()) pos++
        }

        fun value(): Any? {
            skipSpace()
            if (pos >= s.length) fail("fim inesperado")
            return when (val c = s[pos]) {
                '{' -> obj()
                '[' -> list()
                '"' -> string()
                't' -> literal("true", true)
                'f' -> literal("false", false)
                'n' -> literal("null", null)
                else -> if (c == '-' || c.isDigit()) number() else fail("caractere inesperado '$c'")
            }
        }

        private fun literal(word: String, value: Any?): Any? {
            if (!s.startsWith(word, pos)) fail("esperava $word")
            pos += word.length
            return value
        }

        private fun obj(): Map<String, Any?> {
            val map = LinkedHashMap<String, Any?>()
            pos++
            skipSpace()
            if (pos < s.length && s[pos] == '}') { pos++; return map }
            while (true) {
                skipSpace()
                if (pos >= s.length || s[pos] != '"') fail("esperava o nome do campo")
                val key = string()
                skipSpace()
                if (pos >= s.length || s[pos] != ':') fail("esperava ':'")
                pos++
                map[key] = value()
                skipSpace()
                if (pos >= s.length) fail("objeto sem fim")
                when (s[pos++]) {
                    ',' -> continue
                    '}' -> return map
                    else -> fail("esperava ',' ou '}'")
                }
            }
        }

        private fun list(): List<Any?> {
            val list = ArrayList<Any?>()
            pos++
            skipSpace()
            if (pos < s.length && s[pos] == ']') { pos++; return list }
            while (true) {
                list.add(value())
                skipSpace()
                if (pos >= s.length) fail("lista sem fim")
                when (s[pos++]) {
                    ',' -> continue
                    ']' -> return list
                    else -> fail("esperava ',' ou ']'")
                }
            }
        }

        private fun string(): String {
            val out = StringBuilder()
            pos++
            while (true) {
                if (pos >= s.length) fail("texto sem fim")
                val c = s[pos++]
                when (c) {
                    '"' -> return out.toString()
                    '\\' -> {
                        if (pos >= s.length) fail("escape sem fim")
                        when (val e = s[pos++]) {
                            '"', '\\', '/' -> out.append(e)
                            'n' -> out.append('\n')
                            'r' -> out.append('\r')
                            't' -> out.append('\t')
                            'b' -> out.append('\b')
                            'f' -> out.append('\u000C')
                            'u' -> {
                                if (pos + 4 > s.length) fail("escape \\u incompleto")
                                out.append(s.substring(pos, pos + 4).toIntOrNull(16)?.toChar() ?: fail("escape \\u inválido"))
                                pos += 4
                            }
                            else -> fail("escape desconhecido '\\$e'")
                        }
                    }
                    else -> out.append(c)
                }
            }
        }

        private fun number(): Any {
            val start = pos
            if (s[pos] == '-') pos++
            while (pos < s.length && (s[pos].isDigit() || s[pos] in ".eE+-")) pos++
            val text = s.substring(start, pos)
            return text.toLongOrNull() ?: text.toDoubleOrNull() ?: fail("número inválido '$text'")
        }
    }
}

// Leitura tolerante de campos: ausente ou de outro tipo vale o padrão, como nas versões do Ponte que
// não conhecem um campo mais novo.
fun Map<String, Any?>.str(name: String): String = this[name] as? String ?: ""
fun Map<String, Any?>.bool(name: String): Boolean = this[name] as? Boolean ?: false
fun Map<String, Any?>.int(name: String, default: Int = 0): Int = (this[name] as? Number)?.toInt() ?: default
fun Map<String, Any?>.long(name: String): Long = (this[name] as? Number)?.toLong() ?: 0L

@Suppress("UNCHECKED_CAST")
fun Map<String, Any?>.objects(name: String): List<Map<String, Any?>> =
    (this[name] as? List<*>)?.filterIsInstance<Map<*, *>>()?.map { it as Map<String, Any?> } ?: emptyList()
