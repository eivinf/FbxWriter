﻿using System;
using System.Text;
using System.IO;
using System.IO.Compression;

namespace Fbx
{
	/// <summary>
	/// Reads FBX nodes from a binary stream
	/// </summary>
	public class FbxBinaryReader : FbxBinary
	{
		private readonly BinaryReader stream;
		private readonly ErrorLevel errorLevel;

		private delegate object ReadPrimitive(BinaryReader reader);

		/// <summary>
		/// Creates a new reader
		/// </summary>
		/// <param name="stream">The stream to read from</param>
		/// <param name="errorLevel">When to throw an <see cref="FbxException"/></param>
		/// <exception cref="ArgumentException"><paramref name="stream"/> does
		/// not support seeking</exception>
		public FbxBinaryReader(Stream stream, ErrorLevel errorLevel = ErrorLevel.Checked)
		{
			if(stream == null)
				throw new ArgumentNullException(nameof(stream));
			if(!stream.CanSeek)
				throw new ArgumentException(
					"The stream must support seeking. Try reading the data into a buffer first");
			this.stream = new BinaryReader(stream, Encoding.ASCII);
			this.errorLevel = errorLevel;
		}

		// Reads a single property
		object ReadProperty()
		{
			var dataType = (char) stream.ReadByte();
            switch (dataType)
			{
				case 'Y':
					return stream.ReadInt16();
				case 'C':
					return stream.ReadBoolean();
				case 'I':
					return stream.ReadInt32();
				case 'F':
					return stream.ReadSingle();
				case 'D':
					return stream.ReadDouble();
				case 'L':
					return stream.ReadInt64();
				case 'f':
					return ReadArray(br => br.ReadSingle(), typeof(float));
				case 'd':
					return ReadArray(br => br.ReadDouble(), typeof(double));
				case 'l':
					return ReadArray(br => br.ReadInt64(), typeof(long));
				case 'i':
					return ReadArray(br => br.ReadInt32(), typeof(int));
				case 'b':
					return ReadArray(br => br.ReadBoolean(), typeof(bool));
				case 'S':
					var len = stream.ReadInt32();
                    return len == 0 ? "" : Encoding.ASCII.GetString(stream.ReadBytes(len));
				case 'R':
					return stream.ReadBytes(stream.ReadInt32());
				default:
					throw new FbxException(stream.BaseStream.Position - 1,
						"Invalid property data type `" + dataType + "'");
			}
		}

		// Reads an array, decompressing it if required
		Array ReadArray(ReadPrimitive readPrimitive, Type arrayType)
		{
			var len = stream.ReadInt32();
			var encoding = stream.ReadInt32();
			var compressedLen = stream.ReadInt32();
			var ret = Array.CreateInstance(arrayType, len);
			var s = stream;
			var endPos = stream.BaseStream.Position + compressedLen;
			if (encoding != 0)
			{
				if(errorLevel >= ErrorLevel.Strict)
				{
					if(encoding != 1)
						throw new FbxException(stream.BaseStream.Position - 1,
							"Invalid compression encoding (must be 0 or 1)");
					var cmf = stream.ReadByte();
					if((cmf & 0xF) != 8 || (cmf >> 4) > 7)
						throw new FbxException(stream.BaseStream.Position - 1,
							"Invalid compression format " + cmf);
					var flg = stream.ReadByte();
					if(((cmf << 8) + flg) % 31 != 0)
						throw new FbxException(stream.BaseStream.Position - 1,
							"Invalid compression FCHECK");
					if((flg & (1 << 5)) != 0)
						throw new FbxException(stream.BaseStream.Position - 1,
							"Invalid compression flags; dictionary not supported");
                } else
				{
					stream.BaseStream.Position += 2;
				}
				var codec = new DeflateWithChecksum(stream.BaseStream, CompressionMode.Decompress);
				s = new BinaryReader(codec);
			}
			try
			{
				for (int i = 0; i < len; i++)
					ret.SetValue(readPrimitive(s), i);
			}
			catch (InvalidDataException)
			{
				throw new FbxException(stream.BaseStream.Position - 1,
                    "Compressed data was malformed");
			}
			if (encoding != 0)
			{
				if (errorLevel >= ErrorLevel.Checked)
				{
					stream.BaseStream.Position = endPos - sizeof(int);
					var checksumBytes = new byte[sizeof(int)];
					stream.BaseStream.Read(checksumBytes, 0, checksumBytes.Length);
					int checksum = 0;
					for (int i = 0; i < checksumBytes.Length; i++)
						checksum = (checksum << 8) + checksumBytes[i];
					if(checksum != ((DeflateWithChecksum)s.BaseStream).Checksum)
						throw new FbxException(stream.BaseStream.Position,
							"Compressed data has invalid checksum");
				}
				else
				{
					stream.BaseStream.Position = endPos;
				}
			}
			return ret;
		}

		// Reads a single node
		FbxNode ReadNode()
		{
			var endOffset = stream.ReadInt32();
			var numProperties = stream.ReadInt32();
			var propertyListLen = stream.ReadInt32();
			var nameLen = stream.ReadByte();
            var name = nameLen == 0 ? "" : Encoding.ASCII.GetString(stream.ReadBytes(nameLen));

			if (endOffset == 0)
			{
				// The end offset should only be 0 in a null node
				if(errorLevel >= ErrorLevel.Checked && (numProperties != 0 || propertyListLen != 0 || !string.IsNullOrEmpty(name)))
					throw new FbxException(stream.BaseStream.Position,
						"Invalid node; expected NULL record");
				return null;
			}

			var node = new FbxNode {Name = name};

			var propertyEnd = stream.BaseStream.Position + propertyListLen;
			// Read properties
			for (int i = 0; i < numProperties; i++)
				node.Properties.Add(ReadProperty());

			if(errorLevel >= ErrorLevel.Checked && stream.BaseStream.Position != propertyEnd)
				throw new FbxException(stream.BaseStream.Position,
					"Too many bytes in property list, end point is " + propertyEnd);

			// Read nested nodes
			var listLen = endOffset - stream.BaseStream.Position;
			if(errorLevel >= ErrorLevel.Checked && listLen < 0)
				throw new FbxException(stream.BaseStream.Position,
					"Node has invalid end point");
			if (listLen > 0)
			{
				FbxNode nested;
				do
				{
					nested = ReadNode();
					node.Nodes.Add(nested);
				} while (nested != null);
				if (errorLevel >= ErrorLevel.Checked && stream.BaseStream.Position != endOffset)
					throw new FbxException(stream.BaseStream.Position,
						"Too many bytes in node, end point is " + endOffset);
			}
			return node;
		}

		/// <summary>
		/// Reads an FBX document from the stream
		/// </summary>
		/// <param name="version">The file's reported version number</param>
		/// <returns>The top-level node</returns>
		/// <exception cref="FbxException">The FBX data was malformed
		/// for the reader's error level</exception>
		public FbxNode Read(out int version)
		{
			// Read header
			bool validHeader = ReadHeader(stream.BaseStream);
			if (errorLevel >= ErrorLevel.Strict && !validHeader)
				throw new FbxException(stream.BaseStream.Position,
					"Invalid header string");
			version = stream.ReadInt32();

			// Read nodes
			var node = new FbxNode();
			var dataPos = stream.BaseStream.Position;
			FbxNode nested;
			do
			{
				nested = ReadNode();
				if(nested != null)
					node.Nodes.Add(nested);
			} while (nested != null);

			// Read footer code
			var footerCode = new byte[footerCodeSize];
			stream.BaseStream.Read(footerCode, 0, footerCode.Length);
			if (errorLevel >= ErrorLevel.Strict)
			{
				var validCode = GenerateFooterCode(node, dataPos);
				if(!CheckEqual(footerCode, validCode))
					throw new FbxException(stream.BaseStream.Position - footerCodeSize,
						"Incorrect footer code");
			}

			// Read footer extension
			dataPos = stream.BaseStream.Position;
			var validFooterExtension = CheckFooter(stream, version);
			if(errorLevel >= ErrorLevel.Strict && !validFooterExtension)
				throw new FbxException(dataPos, "Invalid footer");

			// Account for existence top level node or not
			if (node.Nodes.Count == 1 && node.Properties.Count == 0 && string.IsNullOrEmpty(node.Name))
				node = node.Nodes[0];
			return node;
		}
	}
}
