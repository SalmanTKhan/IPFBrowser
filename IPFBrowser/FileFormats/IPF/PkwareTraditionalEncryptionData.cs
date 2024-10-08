﻿// https://github.com/adamhathcock/sharpcompress/blob/master/SharpCompress/Common/Zip/PkwareTraditionalEncryptionData.cs
// License: MIT (https://github.com/adamhathcock/sharpcompress/blob/master/LICENSE.txt)

using System;
using System.IO;

namespace IPFBrowser.FileFormats.IPF
{
	internal class PkwareTraditionalEncryptionData
	{
		private static readonly CRC32 crc32 = new CRC32();
		private readonly UInt32[] _Keys = { 0x12345678, 0x23456789, 0x34567890 };

		public PkwareTraditionalEncryptionData(string password)
		{
			Initialize(password);
		}

		private byte MagicByte
		{
			get
			{
				ushort t = (ushort)((ushort)(_Keys[2] & 0xFFFF) | 2);
				return (byte)((t * (t ^ 1)) >> 8);
			}
		}

		public byte[] Decrypt(byte[] cipherText, int length)
		{
			if (length > cipherText.Length)
				throw new ArgumentOutOfRangeException("length", "Bad length during Decryption: the length parameter must be smaller than or equal to the size of the destination array.");

			var plainText = new byte[length];
			for (int i = 0; i < length; i++)
			{
				if ((i % 2) != 0)
				{
					plainText[i] = cipherText[i];
				}
				else
				{
					var c = cipherText[i];
					var k = (ushort)((ushort)(_Keys[2] & 0xFFFF) | 2);
					c ^= (byte)(((k * (k ^ 1)) >> 8) & 0xFF);
					UpdateKeys(c);
					plainText[i] = c;
				}
			}
			return plainText;
		}

		public byte[] Encrypt(byte[] plainText, int length)
		{
			if (plainText == null)
				throw new ArgumentNullException("plaintext");

			if (length > plainText.Length)
				throw new ArgumentOutOfRangeException("length", "Bad length during Encryption: The length parameter must be smaller than or equal to the size of the destination array.");

			var cipherText = new byte[length];
			for (int i = 0; i < length; i++)
			{
                if ((i % 2) != 0)
                {
					cipherText[i] = plainText[i];
                }
				else
				{
                    byte C = plainText[i];
                    cipherText[i] = (byte)(plainText[i] ^ MagicByte);
                    UpdateKeys(C);
                }                
			}
			return cipherText;
		}

		private void Initialize(string password)
		{
			foreach (var p in password)
				UpdateKeys((byte)p);
		}

		private void UpdateKeys(byte byteValue)
		{
			_Keys[0] = (UInt32)crc32.ComputeCrc32((int)_Keys[0], byteValue);
			_Keys[1] = _Keys[1] + (byte)_Keys[0];
			_Keys[1] = _Keys[1] * 0x08088405 + 1;
			_Keys[2] = (UInt32)crc32.ComputeCrc32((int)_Keys[2], (byte)(_Keys[1] >> 24));
		}

		#region crc32
		internal class CRC32
		{
			private const int BUFFER_SIZE = 8192;
			private static readonly UInt32[] crc32Table;
			private UInt32 runningCrc32Result = 0xFFFFFFFF;
			private Int64 totalBytesRead;

			static CRC32()
			{
				unchecked
				{
					// PKZip specifies CRC32 with a polynomial of 0xEDB88320;
					// This is also the CRC-32 polynomial used bby Ethernet, FDDI,
					// bzip2, gzip, and others.
					// Often the polynomial is shown reversed as 0x04C11DB7.
					// For more details, see http://en.wikipedia.org/wiki/Cyclic_redundancy_check
					UInt32 dwPolynomial = 0xEDB88320;
					UInt32 i, j;

					crc32Table = new UInt32[256];

					UInt32 dwCrc;
					for (i = 0; i < 256; i++)
					{
						dwCrc = i;
						for (j = 8; j > 0; j--)
						{
							if ((dwCrc & 1) == 1)
							{
								dwCrc = (dwCrc >> 1) ^ dwPolynomial;
							}
							else
							{
								dwCrc >>= 1;
							}
						}
						crc32Table[i] = dwCrc;
					}
				}
			}

			/// <summary>
			/// indicates the total number of bytes read on the CRC stream.
			/// This is used when writing the ZipDirEntry when compressing files.
			/// </summary>
			public Int64 TotalBytesRead
			{
				get { return totalBytesRead; }
			}

			/// <summary>
			/// Indicates the current CRC for all blocks slurped in.
			/// </summary>
			public Int32 Crc32Result
			{
				get
				{
					// return one's complement of the running result
					return unchecked((Int32)(~runningCrc32Result));
				}
			}

			/// <summary>
			/// Returns the CRC32 for the specified stream.
			/// </summary>
			/// <param name="input">The stream over which to calculate the CRC32</param>
			/// <returns>the CRC32 calculation</returns>
			public Int32 GetCrc32(Stream input)
			{
				return GetCrc32AndCopy(input, null);
			}

			/// <summary>
			/// Returns the CRC32 for the specified stream, and writes the input into the
			/// output stream.
			/// </summary>
			/// <param name="input">The stream over which to calculate the CRC32</param>
			/// <param name="output">The stream into which to deflate the input</param>
			/// <returns>the CRC32 calculation</returns>
			public Int32 GetCrc32AndCopy(Stream input, Stream output)
			{
				if (input == null)
					throw new Exception("The input stream must not be null.");

				unchecked
				{
					//UInt32 crc32Result;
					//crc32Result = 0xFFFFFFFF;
					var buffer = new byte[BUFFER_SIZE];
					int readSize = BUFFER_SIZE;

					totalBytesRead = 0;
					int count = input.Read(buffer, 0, readSize);
					if (output != null) output.Write(buffer, 0, count);
					totalBytesRead += count;
					while (count > 0)
					{
						SlurpBlock(buffer, 0, count);
						count = input.Read(buffer, 0, readSize);
						if (output != null) output.Write(buffer, 0, count);
						totalBytesRead += count;
					}

					return (Int32)(~runningCrc32Result);
				}
			}


			/// <summary>
			/// Get the CRC32 for the given (word,byte) combo.  This is a computation
			/// defined by PKzip.
			/// </summary>
			/// <param name="W">The word to start with.</param>
			/// <param name="B">The byte to combine it with.</param>
			/// <returns>The CRC-ized result.</returns>
			public Int32 ComputeCrc32(Int32 W, byte B)
			{
				return _InternalComputeCrc32((UInt32)W, B);
			}

			internal Int32 _InternalComputeCrc32(UInt32 W, byte B)
			{
				return (Int32)(crc32Table[(W ^ B) & 0xFF] ^ (W >> 8));
			}

			/// <summary>
			/// Update the value for the running CRC32 using the given block of bytes.
			/// This is useful when using the CRC32() class in a Stream.
			/// </summary>
			/// <param name="block">block of bytes to slurp</param>
			/// <param name="offset">starting point in the block</param>
			/// <param name="count">how many bytes within the block to slurp</param>
			public void SlurpBlock(byte[] block, int offset, int count)
			{
				if (block == null)
					throw new Exception("The data buffer must not be null.");

				for (int i = 0; i < count; i++)
				{
					int x = offset + i;
					runningCrc32Result = ((runningCrc32Result) >> 8) ^
										 crc32Table[(block[x]) ^ ((runningCrc32Result) & 0x000000FF)];
				}
				totalBytesRead += count;
			}


			// pre-initialize the crc table for speed of lookup.


			private uint gf2_matrix_times(uint[] matrix, uint vec)
			{
				uint sum = 0;
				int i = 0;
				while (vec != 0)
				{
					if ((vec & 0x01) == 0x01)
						sum ^= matrix[i];
					vec >>= 1;
					i++;
				}
				return sum;
			}

			private void gf2_matrix_square(uint[] square, uint[] mat)
			{
				for (int i = 0; i < 32; i++)
					square[i] = gf2_matrix_times(mat, mat[i]);
			}


			/// <summary>
			/// Combines the given CRC32 value with the current running total.
			/// </summary>
			/// <remarks>
			/// This is useful when using a divide-and-conquer approach to calculating a CRC.
			/// Multiple threads can each calculate a CRC32 on a segment of the data, and then
			/// combine the individual CRC32 values at the end.
			/// </remarks>
			/// <param name="crc">the crc value to be combined with this one</param>
			/// <param name="length">the length of data the CRC value was calculated on</param>
			public void Combine(int crc, int length)
			{
				var even = new uint[32]; // even-power-of-two zeros operator
				var odd = new uint[32]; // odd-power-of-two zeros operator

				if (length == 0)
					return;

				uint crc1 = ~runningCrc32Result;
				var crc2 = (uint)crc;

				// put operator for one zero bit in odd
				odd[0] = 0xEDB88320; // the CRC-32 polynomial
				uint row = 1;
				for (int i = 1; i < 32; i++)
				{
					odd[i] = row;
					row <<= 1;
				}

				// put operator for two zero bits in even
				gf2_matrix_square(even, odd);

				// put operator for four zero bits in odd
				gf2_matrix_square(odd, even);

				var len2 = (uint)length;

				// apply len2 zeros to crc1 (first square will put the operator for one
				// zero byte, eight zero bits, in even)
				do
				{
					// apply zeros operator for this bit of len2
					gf2_matrix_square(even, odd);

					if ((len2 & 1) == 1)
						crc1 = gf2_matrix_times(even, crc1);
					len2 >>= 1;

					if (len2 == 0)
						break;

					// another iteration of the loop with odd and even swapped
					gf2_matrix_square(odd, even);
					if ((len2 & 1) == 1)
						crc1 = gf2_matrix_times(odd, crc1);
					len2 >>= 1;
				} while (len2 != 0);

				crc1 ^= crc2;

				runningCrc32Result = ~crc1;

				//return (int) crc1;
				return;
			}


			// private member vars
		}
		#endregion
	}
}
