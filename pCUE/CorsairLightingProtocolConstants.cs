using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Runtime.InteropServices;

namespace pCUE
{
     class CorsairLightingProtocolConstants
    {
        public const int COMMAND_SIZE = 64;
        public const int RESPONSE_SIZE = 16;
        public const int READ_STATUS = 0x01;
        public const int READ_FIRMWARE_VERSION = 0x02;
        public const int READ_DEVICE_ID = 0x03;
        public const int WRITE_DEVICE_ID = 0x04;
        public const int START_FIRMWARE_UPDATE = 0x05;
        public const int READ_BOOTLOADER_VERSION = 0x06;
        public const int WRITE_TEST_FLAG = 0x07;
        public const int READ_TEMPERATURE_MASK = 0x10;
        public const int READ_TEMPERATURE_VALUE = 0x11;
        public const int READ_VOLTAGE_VALUE = 0x12;
        public const int READ_FAN_MASK = 0x20;
        public const int READ_FAN_SPEED = 0x21;
        public const int READ_FAN_POWER = 0x22;
        public const int WRITE_FAN_POWER = 0x23;
        public const int WRITE_FAN_SPEED = 0x24;
        public const int WRITE_FAN_CURVE = 0x25;
        public const int WRITE_FAN_EXTERNAL_TEMP = 0x26;
        public const int WRITE_FAN_FORCE_THREE_PIN_MODE = 0x27;
        public const int WRITE_FAN_DETECTION_TYPE = 0x28;
        public const int READ_FAN_DETECTION_TYPE = 0x29;
        public const int READ_LED_STRIP_MASK = 0x30;
        public const int WRITE_LED_RGB_VALUE = 0x31;
        public const int WRITE_LED_COLOR_VALUES = 0x32;
        public const int WRITE_LED_TRIGGER = 0x33;
        public const int WRITE_LED_CLEAR = 0x34;
        public const int WRITE_LED_GROUP_SET = 0x35;
        public const int WRITE_LED_EXTERNAL_TEMP = 0x36;
        public const int WRITE_LED_GROUPS_CLEAR = 0x37;
        public const int WRITE_LED_MODE = 0x38;
        public const int WRITE_LED_BRIGHTNESS = 0x39;
        public const int WRITE_LED_COUNT = 0x3A;
        public const int WRITE_LED_PORT_TYPE = 0x3B;
        public const int PROTOCOL_RESPONSE_OK = 0x00;
        public const int PROTOCOL_RESPONSE_ERROR = 0x01;
       

        //Byte 1-6 describes mode for each fan (6 fan connectors are available)
        //Byte 0 is always zero
        public const int Fan_Auto_Mode = 0;
        public const int Fan_3pin_Mode = 1;
        public const int Fan_4pin_Mode = 2;

        public struct Command
        {          
            [StructLayout(LayoutKind.Explicit)]
            unsafe struct Command_Union
            {
                [FieldOffset(0)]
                public byte command;
                [FieldOffset(1)]              
                public fixed byte data[COMMAND_SIZE - 1];     //me to fixed ola ok       
                [FieldOffset(0)]
                public fixed byte raw[COMMAND_SIZE];
            }
        }

    }
}
