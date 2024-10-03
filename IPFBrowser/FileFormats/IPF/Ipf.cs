// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// 
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
// 
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace IPFBrowser.FileFormats.IPF
{
	public class Ipf
	{
		private object _extractLock = new object();
		private readonly Stream _stream;
		private readonly BinaryReader _br;

		public string FilePath { get; private set; }
		public List<IpfFile> Files { get; private set; }
		public IpfFooter Footer { get; private set; }

		public Ipf(string filePath)
		{
			_stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
			_br = new BinaryReader(_stream);

			this.FilePath = filePath;
			this.Load();
		}

		public Ipf(uint oldVersion = 0, uint newVersion = 1000000)
		{
			this.Files = [];
			this.Footer = new IpfFooter
			{
				// Set default footer values for a new IPF
				FileCount = 0,
				FileTableOffset = 0, // Will be calculated later
				FileRemovedCount = 0,
				RemovedFileTableOffset = 0,
				OldVersion = oldVersion,
				NewVersion = newVersion
			};
		}

		public void Close()
		{
			_stream?.Dispose();
			_br?.Dispose();
		}

		public void Load()
		{
			_stream.Position = _stream.Length - 0x18;

			this.Footer = new IpfFooter
			{
				FileCount = _br.ReadUInt16(),
				FileTableOffset = _br.ReadUInt32(),
				FileRemovedCount = _br.ReadUInt16(),
				RemovedFileTableOffset = _br.ReadUInt32(),
				Signature = _br.ReadBytes(4),
				OldVersion = _br.ReadUInt32(),
				NewVersion = _br.ReadUInt32()
			};

			_stream.Position = this.Footer.FileTableOffset;

			this.Files = [];
			for (int i = 0; i < this.Footer.FileCount; i++)
			{
				var ipfFile = new IpfFile(this);

				var pathLength = _br.ReadUInt16();
				ipfFile.Checksum = _br.ReadUInt32();
				ipfFile.SizeCompressed = _br.ReadUInt32();
				ipfFile.SizeUncompressed = _br.ReadUInt32();
				ipfFile.Offset = _br.ReadUInt32();

				var length = _br.ReadUInt16();
				ipfFile.PackFileName = new string(_br.ReadChars(length));

				var path = new string(_br.ReadChars(pathLength));
				ipfFile.Path = path.Replace("\\", "/");
				ipfFile.FullPath = Path.Combine(ipfFile.PackFileName, path).Replace("\\", "/");

				this.Files.Add(ipfFile);
			}
		}

		public bool Save(string filePath)
		{
			var tempPath = Path.GetDirectoryName(filePath) + "\\~" + Path.GetFileName(filePath);

			if (File.Exists(tempPath))
			{
				File.Delete(tempPath);
			}

			try
			{
				using var outputStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
				using var bw = new BinaryWriter(outputStream);

				uint currentPosition = 0;

				foreach (var file in this.Files)
				{
					if (!file.IsModified)
					{
						bw.Write(ReadData(file.Offset, (int)file.SizeCompressed));
						bw.Flush();

						// update the offset for use in writing the file table later, nothing else needs to be changed
						file.Offset = currentPosition;
						currentPosition += file.SizeCompressed;
					}
					else
					{
						file.SizeUncompressed = (uint)file.GetData().Length;

						var compressedData = file.Compress();
						file.SizeCompressed = (uint)compressedData.Length;
						file.Checksum = CRC32.CalculateCRC32(0, compressedData);

						bw.Write(compressedData);
						bw.Flush();

						file.Offset = currentPosition;
						currentPosition += file.SizeCompressed;
					}
				}

				IpfFooter newFooter = new()
				{
					FileCount = (ushort)this.Files.Count,
					FileTableOffset = currentPosition,
					FileRemovedCount = this.Footer.FileRemovedCount,
					RemovedFileTableOffset = this.Footer.RemovedFileTableOffset,
					Signature = this.Footer.Signature,
					OldVersion = this.Footer.OldVersion,
					NewVersion = this.Footer.NewVersion
				};

				// Now we write the file table

				foreach (var file in this.Files)
				{
					bw.Write((ushort)file.Path.Length);
					bw.Write((uint)file.Checksum);
					bw.Write((uint)file.SizeCompressed);
					bw.Write((uint)file.SizeUncompressed);
					bw.Write((uint)file.Offset);
					bw.Write((ushort)file.PackFileName.Length);
					bw.WriteFixedString(file.PackFileName, file.PackFileName.Length);
					bw.WriteFixedString(file.Path, file.Path.Length);
					bw.Flush();
				}

				// Finally we write the footer

				bw.Write((ushort)newFooter.FileCount);
				bw.Write((uint)newFooter.FileTableOffset);
				bw.Write((ushort)newFooter.FileRemovedCount);
				bw.Write((uint)newFooter.RemovedFileTableOffset);
				bw.Write(newFooter.Signature);
				bw.Write((uint)newFooter.OldVersion);
				bw.Write((uint)newFooter.NewVersion);
				bw.Flush();
				bw.Close();

				if (filePath == this.FilePath)
				{
					// Overwriting the open file, have to reopen the file
					_br.Close();
					_stream.Close();
				}

				if (File.Exists(filePath))
				{
					File.Delete(filePath);
				}
				File.Move(tempPath, filePath);

				return filePath == this.FilePath;
			}
			catch (IOException ex)
			{
				//MessageBox.Show("Cannot save to this file. This file may be open in another application.");
				Trace.TraceError("Cannot save to this file. This file may be open in another application.");
			}
			catch (Exception ex)
			{
				//MessageBox.Show("Unable to save the file");
				Trace.TraceError("Unable to save the file");
			}

			if (File.Exists(tempPath))
			{
				File.Delete(tempPath);
			}

			return false;
		}

		public byte[] ReadData(long offset, int length)
		{
			byte[] data;

			lock (_extractLock)
			{
				_stream.Position = offset;
				data = _br.ReadBytes(length);
			}

			return data;
		}

		public void AddFolder(string parentFolder)
		{
			foreach (var folderPath in Directory.EnumerateDirectories(parentFolder, "*.ipf", SearchOption.AllDirectories))
			{
				var packFileName = Path.GetFileName(folderPath);
				AddFolder(packFileName, folderPath);
			}
		}

		public void AddFolder(string packFileName, string folderPath)
		{
			// Ensure the folder path ends with a directory separator
			if (!folderPath.EndsWith(Path.DirectorySeparatorChar.ToString()))
			{
				folderPath += Path.DirectorySeparatorChar;
			}

			// Recursively add files from the folder and its subfolders
			foreach (var filePath in Directory.EnumerateFiles(folderPath, "*", SearchOption.AllDirectories))
			{
				// Get the relative path within the folder
				var relativePath = filePath.Substring(folderPath.Length).Replace("\\", "/");

				// Read the file data
				var data = File.ReadAllBytes(filePath);

				// Add the file to the IPF
				AddFile(packFileName, relativePath, data);
			}
		}

		public void AddFile(string packFileName, string path, byte[] data)
		{
			var ipfFile = new IpfFile(this, true)
			{
				PackFileName = packFileName,
				Path = path.Replace("\\", "/"),
				FullPath = Path.Combine(packFileName, path).Replace("\\", "/"),
				Content = data,
			};
			this.Files.Add(ipfFile);
		}
	}

	public class IpfFile
	{
		private readonly string[] _noCompression = new[] { ".jpg", ".jpeg", ".fsb", ".mp3" };

		public Ipf Ipf { get; set; }
		public string PackFileName { get; set; }
		public string Path { get; set; }
		public string FullPath { get; set; }
		public uint Offset { get; set; }
		public uint SizeCompressed { get; set; }
		public uint SizeUncompressed { get; set; }
		public uint Checksum { get; set; }
		public bool IsModified { get; set; }
		public byte[] Content { get; set; }

		public IpfFile(Ipf ipf, bool isModified = false)
		{
			this.Ipf = ipf;
			this.IsModified = isModified;
		}
		public byte[] GetData()
		{
			if (IsModified)
			{
				return Content;
			}
			else
			{
				var ext = System.IO.Path.GetExtension(this.Path);
				var data = this.Ipf.ReadData(this.Offset, (int)this.SizeCompressed);

				if (_noCompression.Contains(ext.ToLowerInvariant()))
				{
					return data;
				}
				return Decompress(data);
			}
		}

		private byte[] Decompress(byte[] data)
		{
			if (this.Ipf.Footer.NewVersion > 11000 || this.Ipf.Footer.NewVersion == 0)
			{
				var pkw = new PkwareTraditionalEncryptionData("ofO1a0ueXA? [\xFFs h %?");
				data = pkw.Decrypt(data, data.Length);
			}

			using var msOut = new MemoryStream();
			using var msIn = new MemoryStream(data);
			using var deflate = new DeflateStream(msIn, CompressionMode.Decompress);
			deflate.CopyTo(msOut);
			return msOut.ToArray();
		}

		public byte[] Compress()
		{
			if (!IsModified)
			{
				// data is already compressed, just return the data
				return this.Ipf.ReadData(this.Offset, (int)this.SizeCompressed);
			}

			var ext = System.IO.Path.GetExtension(this.Path);
			if (_noCompression.Contains(ext.ToLowerInvariant()))
			{
				// does not require compression or encryption               
				return Content;
			}

			byte[] bytes = null;

			using (var ms = new MemoryStream())
			{
				using (var deflate = new DeflateStream(ms, CompressionMode.Compress, true))
				{
					deflate.Write(Content, 0, Content.Length);
				}

				bytes = ms.ToArray();
			}

			byte[] encryptedBytes = null;

			if (this.Ipf.Footer.NewVersion > 11000 || this.Ipf.Footer.NewVersion == 0)
			{
				var pkw = new PkwareTraditionalEncryptionData("ofO1a0ueXA? [\xFFs h %?");
				encryptedBytes = pkw.Encrypt(bytes, bytes.Length);
			}

			return encryptedBytes;
		}
	}


	public static class CRC32
	{
		static readonly uint[] crc32_tab = {
			0x00000000, 0x77073096, 0xee0e612c, 0x990951ba, 0x076dc419, 0x706af48f,
			0xe963a535, 0x9e6495a3, 0x0edb8832, 0x79dcb8a4, 0xe0d5e91e, 0x97d2d988,
			0x09b64c2b, 0x7eb17cbd, 0xe7b82d07, 0x90bf1d91, 0x1db71064, 0x6ab020f2,
			0xf3b97148, 0x84be41de, 0x1adad47d, 0x6ddde4eb, 0xf4d4b551, 0x83d385c7,
			0x136c9856, 0x646ba8c0, 0xfd62f97a, 0x8a65c9ec, 0x14015c4f, 0x63066cd9,
			0xfa0f3d63, 0x8d080df5, 0x3b6e20c8, 0x4c69105e, 0xd56041e4, 0xa2677172,
			0x3c03e4d1, 0x4b04d447, 0xd20d85fd, 0xa50ab56b, 0x35b5a8fa, 0x42b2986c,
			0xdbbbc9d6, 0xacbcf940, 0x32d86ce3, 0x45df5c75, 0xdcd60dcf, 0xabd13d59,
			0x26d930ac, 0x51de003a, 0xc8d75180, 0xbfd06116, 0x21b4f4b5, 0x56b3c423,
			0xcfba9599, 0xb8bda50f, 0x2802b89e, 0x5f058808, 0xc60cd9b2, 0xb10be924,
			0x2f6f7c87, 0x58684c11, 0xc1611dab, 0xb6662d3d, 0x76dc4190, 0x01db7106,
			0x98d220bc, 0xefd5102a, 0x71b18589, 0x06b6b51f, 0x9fbfe4a5, 0xe8b8d433,
			0x7807c9a2, 0x0f00f934, 0x9609a88e, 0xe10e9818, 0x7f6a0dbb, 0x086d3d2d,
			0x91646c97, 0xe6635c01, 0x6b6b51f4, 0x1c6c6162, 0x856530d8, 0xf262004e,
			0x6c0695ed, 0x1b01a57b, 0x8208f4c1, 0xf50fc457, 0x65b0d9c6, 0x12b7e950,
			0x8bbeb8ea, 0xfcb9887c, 0x62dd1ddf, 0x15da2d49, 0x8cd37cf3, 0xfbd44c65,
			0x4db26158, 0x3ab551ce, 0xa3bc0074, 0xd4bb30e2, 0x4adfa541, 0x3dd895d7,
			0xa4d1c46d, 0xd3d6f4fb, 0x4369e96a, 0x346ed9fc, 0xad678846, 0xda60b8d0,
			0x44042d73, 0x33031de5, 0xaa0a4c5f, 0xdd0d7cc9, 0x5005713c, 0x270241aa,
			0xbe0b1010, 0xc90c2086, 0x5768b525, 0x206f85b3, 0xb966d409, 0xce61e49f,
			0x5edef90e, 0x29d9c998, 0xb0d09822, 0xc7d7a8b4, 0x59b33d17, 0x2eb40d81,
			0xb7bd5c3b, 0xc0ba6cad, 0xedb88320, 0x9abfb3b6, 0x03b6e20c, 0x74b1d29a,
			0xead54739, 0x9dd277af, 0x04db2615, 0x73dc1683, 0xe3630b12, 0x94643b84,
			0x0d6d6a3e, 0x7a6a5aa8, 0xe40ecf0b, 0x9309ff9d, 0x0a00ae27, 0x7d079eb1,
			0xf00f9344, 0x8708a3d2, 0x1e01f268, 0x6906c2fe, 0xf762575d, 0x806567cb,
			0x196c3671, 0x6e6b06e7, 0xfed41b76, 0x89d32be0, 0x10da7a5a, 0x67dd4acc,
			0xf9b9df6f, 0x8ebeeff9, 0x17b7be43, 0x60b08ed5, 0xd6d6a3e8, 0xa1d1937e,
			0x38d8c2c4, 0x4fdff252, 0xd1bb67f1, 0xa6bc5767, 0x3fb506dd, 0x48b2364b,
			0xd80d2bda, 0xaf0a1b4c, 0x36034af6, 0x41047a60, 0xdf60efc3, 0xa867df55,
			0x316e8eef, 0x4669be79, 0xcb61b38c, 0xbc66831a, 0x256fd2a0, 0x5268e236,
			0xcc0c7795, 0xbb0b4703, 0x220216b9, 0x5505262f, 0xc5ba3bbe, 0xb2bd0b28,
			0x2bb45a92, 0x5cb36a04, 0xc2d7ffa7, 0xb5d0cf31, 0x2cd99e8b, 0x5bdeae1d,
			0x9b64c2b0, 0xec63f226, 0x756aa39c, 0x026d930a, 0x9c0906a9, 0xeb0e363f,
			0x72076785, 0x05005713, 0x95bf4a82, 0xe2b87a14, 0x7bb12bae, 0x0cb61b38,
			0x92d28e9b, 0xe5d5be0d, 0x7cdcefb7, 0x0bdbdf21, 0x86d3d2d4, 0xf1d4e242,
			0x68ddb3f8, 0x1fda836e, 0x81be16cd, 0xf6b9265b, 0x6fb077e1, 0x18b74777,
			0x88085ae6, 0xff0f6a70, 0x66063bca, 0x11010b5c, 0x8f659eff, 0xf862ae69,
			0x616bffd3, 0x166ccf45, 0xa00ae278, 0xd70dd2ee, 0x4e048354, 0x3903b3c2,
			0xa7672661, 0xd06016f7, 0x4969474d, 0x3e6e77db, 0xaed16a4a, 0xd9d65adc,
			0x40df0b66, 0x37d83bf0, 0xa9bcae53, 0xdebb9ec5, 0x47b2cf7f, 0x30b5ffe9,
			0xbdbdf21c, 0xcabac28a, 0x53b39330, 0x24b4a3a6, 0xbad03605, 0xcdd70693,
			0x54de5729, 0x23d967bf, 0xb3667a2e, 0xc4614ab8, 0x5d681b02, 0x2a6f2b94,
			0xb40bbe37, 0xc30c8ea1, 0x5a05df1b, 0x2d02ef8d
		};


		/// <summary>
		/// Calculate the crc32 checksum of the given memory block.
		/// </summary>
		/// <param name="crc">The start value for the crc</param>
		/// <param name="buf">Pointer to the memory block</param>
		/// <param name="size">Number of bytes</param>
		public static unsafe uint CalculateCRC32(uint crc, byte[] buf)
		{
			crc = crc ^ ~0U; //0xFFFFFFFF
			foreach (byte b in buf)
				crc = crc32_tab[(crc ^ b) & 0xFF] ^ (crc >> 8);

			return crc ^ ~0U; //0xFFFFFFFF
		}
	}

	public class IpfFooter
	{
		public ushort FileCount { get; set; }
		public uint NewVersion { get; set; }
		public uint OldVersion { get; set; }
		public uint FileTableOffset { get; set; }
		public ushort FileRemovedCount { get; set; }
		public uint RemovedFileTableOffset { get; set; }
		public byte[] Signature { get; set; } = new byte[] { 0x50, 0x4B, 0x05, 0x06 }; // PK\x05\x06

	}
}