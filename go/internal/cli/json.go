package cli

import (
	"bytes"
	"encoding/json"
	"fmt"
	"io"
	"strings"
)

// pythonJSON writes JSON the way the Python tool does, so that both
// print the same: fields in the order of the server, characters outside
// ASCII as they are, ", " and ": " as separators on one line, or two
// spaces per level with indent.
func pythonJSON(raw json.RawMessage, indent bool) (string, error) {
	decoder := json.NewDecoder(bytes.NewReader(raw))
	decoder.UseNumber()
	var output strings.Builder
	if err := writeValue(&output, decoder, indent, 0); err != nil {
		return "", fmt.Errorf("cannot print the JSON of the server: %w", err)
	}
	return output.String(), nil
}

func writeValue(
	output *strings.Builder, decoder *json.Decoder, indent bool, level int,
) error {
	token, err := decoder.Token()
	if err != nil {
		return err
	}
	return writeToken(output, decoder, token, indent, level)
}

func writeToken(
	output *strings.Builder, decoder *json.Decoder, token json.Token,
	indent bool, level int,
) error {
	switch value := token.(type) {
	case nil:
		output.WriteString("null")
	case bool:
		if value {
			output.WriteString("true")
		} else {
			output.WriteString("false")
		}
	case json.Number:
		output.WriteString(value.String())
	case string:
		writeString(output, value)
	case json.Delim:
		object := value == '{'
		closing := "]"
		if object {
			closing = "}"
		}
		output.WriteString(value.String())
		count := 0
		for decoder.More() {
			if count > 0 {
				output.WriteString(",")
				if !indent {
					output.WriteString(" ")
				}
			}
			count++
			newLine(output, indent, level+1)
			if object {
				name, err := decoder.Token()
				if err != nil {
					return err
				}
				text, isText := name.(string)
				if !isText {
					return io.ErrUnexpectedEOF
				}
				writeString(output, text)
				output.WriteString(": ")
			}
			if err := writeValue(output, decoder, indent, level+1); err != nil {
				return err
			}
		}
		if _, err := decoder.Token(); err != nil {
			return err
		}
		if count > 0 {
			newLine(output, indent, level)
		}
		output.WriteString(closing)
	}
	return nil
}

func newLine(output *strings.Builder, indent bool, level int) {
	if indent {
		output.WriteString("\n")
		output.WriteString(strings.Repeat("  ", level))
	}
}

// writeString writes a JSON string and escapes only what must be.
func writeString(output *strings.Builder, text string) {
	output.WriteByte('"')
	for _, character := range text {
		switch character {
		case '"':
			output.WriteString(`\"`)
		case '\\':
			output.WriteString(`\\`)
		case '\n':
			output.WriteString(`\n`)
		case '\r':
			output.WriteString(`\r`)
		case '\t':
			output.WriteString(`\t`)
		case '\b':
			output.WriteString(`\b`)
		case '\f':
			output.WriteString(`\f`)
		default:
			if character < 0x20 {
				fmt.Fprintf(output, `\u%04x`, character)
			} else {
				output.WriteRune(character)
			}
		}
	}
	output.WriteByte('"')
}
